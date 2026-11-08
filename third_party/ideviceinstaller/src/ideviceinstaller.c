/*
 * ideviceinstaller - Manage apps on iOS devices.
 *
 * Copyright (C) 2010-2023 Nikias Bassen <nikias@gmx.li>
 * Copyright (C) 2010-2015 Martin Szulecki <m.szulecki@libimobiledevice.org>
 *
 * Licensed under the GNU General Public License Version 2
 *
 * This program is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 2 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more profile.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA 02110-1301
 * USA
 */
#ifdef HAVE_CONFIG_H
#include <config.h>
#endif
#include <stdlib.h>
#define _GNU_SOURCE 1
#define __USE_GNU 1
#include <stdio.h>
#include <string.h>
#include <getopt.h>
#include <errno.h>
#include <time.h>
#include <libgen.h>
#include <inttypes.h>
#include <limits.h>
#include <sys/stat.h>
#include <dirent.h>
#ifdef WIN32
#include <io.h>
#include <fcntl.h>
#endif
#ifdef HAVE_UNISTD_H
#include <unistd.h>
#endif
#ifndef WIN32
#include <signal.h>
#endif

#include <libimobiledevice/libimobiledevice.h>
#include <libimobiledevice/lockdown.h>
#include <libimobiledevice/installation_proxy.h>
#include <libimobiledevice/notification_proxy.h>
#include <libimobiledevice/afc.h>

#include <plist/plist.h>

#include <zip.h>

/*
 * Encoding policy
 * ---------------
 * All strings inside this program are UTF-8.  On Windows the command line is
 * converted from UTF-16 (the native, lossless form) to UTF-8 exactly once at
 * startup, and every access to the file system converts back to UTF-16 at the
 * Win32 boundary.  Library output (libplist, libzip) is UTF-8 already, so the
 * complete output of the program is UTF-8 on every platform - independent of
 * the system's ANSI code page and without converting any string twice.
 */
#ifdef WIN32
#include <windows.h>
#define wait_ms(x) Sleep(x)

static wchar_t *utf8_to_wide(const char *s)
{
	wchar_t *w;
	int len;

	if (!s) {
		return NULL;
	}
	len = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, NULL, 0);
	if (len <= 0) {
		return NULL;
	}
	w = (wchar_t*)malloc((size_t)len * sizeof(wchar_t));
	if (!w) {
		return NULL;
	}
	if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, w, len)) {
		free(w);
		return NULL;
	}
	return w;
}

/*
 * Convert a UTF-8 path to the form the Win32 file APIs need.
 *
 * - Relative paths are made absolute first (GetFullPathNameW), so the result
 *   does not depend on the current directory any more.
 * - A path that already carries the \\?\ or \\?\UNC\ prefix is passed through
 *   unchanged, but the directory separators are normalized to backslashes,
 *   because "." and ".." are not resolved by the OS once the prefix is used.
 * - UNC paths (\\server\share\...) are mapped to \\?\UNC\server\share\...,
 *   since the \\.\ and \\?\ forms are the only ones the long path aware file
 *   APIs accept without going through the WOW64/share redirection.
 * - \\.\ device paths are passed through unchanged (they are not network paths).
 * - Any other result that does not fit into MAX_PATH is prefixed with \\?\
 *   as well, so the file APIs this program calls never need the long path
 *   opt-in (longPathAware manifest / machine-wide LongPathsEnabled setting).
 *
 * The returned string is what must be handed to _wfopen/CreateFileW and the
 * file size helpers; it is never used for display.
 */
static wchar_t *path_utf8_to_win32(const char *path);

/* \\?\C:\... style prefix, long path aware form */
static int is_long_path(const wchar_t *w)
{
	return (wcsncmp(w, L"\\\\?\\", 4) == 0);
}

/* \\.\ device namespace (e.g. \\.\pipe\foo); must be passed through unchanged */
static int is_device_path(const wchar_t *w)
{
	return (wcsncmp(w, L"\\\\.\\", 4) == 0);
}

/* \\server\share\... style network path; \\?\ and \\.\ have their own forms */
static int is_unc_path(const wchar_t *w)
{
	return (wcsncmp(w, L"\\\\", 2) == 0) && w[2] != L'?' && w[2] != L'.';
}

static wchar_t *long_path_prefix(const wchar_t *w)
{
	size_t len = wcslen(w);
	wchar_t *res = (wchar_t*)malloc((len + 5) * sizeof(wchar_t));
	if (!res) {
		return NULL;
	}
	memcpy(res, L"\\\\?\\", 4 * sizeof(wchar_t));
	memcpy(res + 4, w, (len + 1) * sizeof(wchar_t));
	return res;
}

static wchar_t *unc_to_long_path(const wchar_t *w)
{
	size_t len = wcslen(w);
	wchar_t *res;

	if (len < 2) {
		return NULL;
	}
	/* "\\?\UNC\" is 8 characters; append the UNC path without its leading
	 * "\\" (len-2 chars) and the terminating NUL => 8 + (len-2) + 1 = len+7 */
	res = (wchar_t*)malloc((len + 7) * sizeof(wchar_t));
	if (!res) {
		return NULL;
	}
	memcpy(res, L"\\\\?\\UNC\\", 8 * sizeof(wchar_t));
	memcpy(res + 8, w + 2, (len - 2 + 1) * sizeof(wchar_t));
	return res;
}

static wchar_t *path_utf8_to_win32(const char *path)
{
	wchar_t *w;
	wchar_t *full;
	wchar_t *res;
	size_t len;
	DWORD n;
	DWORD buflen;

	w = utf8_to_wide(path);
	if (!w) {
		return NULL;
	}

	/* normalize separators; every variant below gets backslashes */
	for (len = 0; w[len]; len++) {
		if (w[len] == L'/') {
			w[len] = L'\\';
		}
	}

	if (is_long_path(w) || is_device_path(w)) {
		return w;
	}
	if (is_unc_path(w)) {
		res = unc_to_long_path(w);
		free(w);
		return res;
	}

	/* make relative paths absolute so the \?\ prefix below is allowed */
	buflen = (DWORD)(2 * len + 64);
	full = (wchar_t*)malloc(buflen * sizeof(wchar_t));
	if (!full) {
		free(w);
		return NULL;
	}
	n = GetFullPathNameW(w, buflen, full, NULL);
	if (n == 0) {
		free(full);
		free(w);
		return NULL;
	}
	if (n >= buflen) {
		/* n is the required buffer size (including the NUL); retry once */
		wchar_t *bigger = (wchar_t*)malloc((size_t)n * sizeof(wchar_t));
		if (!bigger) {
			free(full);
			free(w);
			return NULL;
		}
		free(full);
		full = bigger;
		buflen = n;
		n = GetFullPathNameW(w, buflen, full, NULL);
		if (n == 0 || n >= buflen) {
			free(full);
			free(w);
			return NULL;
		}
	}
	free(w);
	w = full;

	if (is_long_path(w) || is_device_path(w)) {
		return w;
	}
	if (is_unc_path(w)) {
		res = unc_to_long_path(w);
		free(w);
		return res;
	}
	if (wcslen(w) < MAX_PATH) {
		return w;
	}
	res = long_path_prefix(w);
	free(w);
	return res;
}

static char *wide_to_utf8(const wchar_t *w)
{
	char *s;
	int len;

	if (!w) {
		return NULL;
	}
	len = WideCharToMultiByte(CP_UTF8, 0, w, -1, NULL, 0, NULL, NULL);
	if (len <= 0) {
		return NULL;
	}
	s = (char*)malloc((size_t)len);
	if (!s) {
		return NULL;
	}
	if (!WideCharToMultiByte(CP_UTF8, 0, w, -1, s, len, NULL, NULL)) {
		free(s);
		return NULL;
	}
	return s;
}

static char *ansi_to_utf8(const char *ansistr)
{
	wchar_t *w;
	char *s;
	int len;

	if (!ansistr) {
		return NULL;
	}
	len = MultiByteToWideChar(CP_ACP, 0, ansistr, -1, NULL, 0);
	if (len <= 0) {
		return NULL;
	}
	w = (wchar_t*)malloc((size_t)len * sizeof(wchar_t));
	if (!w) {
		return NULL;
	}
	if (!MultiByteToWideChar(CP_ACP, 0, ansistr, -1, w, len)) {
		free(w);
		return NULL;
	}
	s = wide_to_utf8(w);
	free(w);
	return s;
}

/*
 * Replace the ANSI argv prepared by the C runtime with a UTF-8 argv built
 * from the real UTF-16 command line, so that path names are not limited to
 * the characters of the current ANSI code page.
 */
static void win32_argv_to_utf8(int *argc, char ***argv)
{
	LPWSTR *wargv;
	char **uargv;
	int wargc = 0;
	int i;

	wargv = CommandLineToArgvW(GetCommandLineW(), &wargc);
	if (wargv && wargc > 0) {
		uargv = (char**)calloc((size_t)wargc + 1, sizeof(char*));
		if (uargv) {
			for (i = 0; i < wargc; i++) {
				uargv[i] = wide_to_utf8(wargv[i]);
				if (!uargv[i]) {
					uargv[i] = strdup("");
				}
			}
			LocalFree(wargv);
			*argc = wargc;
			*argv = uargv;
			/* The CRT's original argv strings are not freed: they are owned by
			 * the CRT, live for the whole process and are never replaced again. */
			return;
		}
		LocalFree(wargv);
	}
	/* Fallback: keep the invariant of UTF-8 strings even if the command
	 * line could not be re-read. */
	uargv = (char**)calloc((size_t)*argc + 1, sizeof(char*));
	if (!uargv) {
		fprintf(stderr, "WARNING: could not convert the command line to UTF-8; "
			"arguments stay in the ANSI code page.\n");
		return;
	}
	for (i = 0; i < *argc; i++) {
		uargv[i] = ansi_to_utf8((*argv)[i]);
		if (!uargv[i]) {
			uargv[i] = strdup("");
		}
	}
	*argv = uargv;
}

static int win32_console_cp_changed = 0;
static UINT win32_console_cp_saved = 0;

static void win32_console_utf8_restore(void)
{
	if (win32_console_cp_changed) {
		SetConsoleOutputCP(win32_console_cp_saved);
		win32_console_cp_changed = 0;
	}
}

/*
 * Make sure a console shows the UTF-8 output correctly.  When the output is
 * redirected (a pipe, a file, a GUI) nothing is changed and plain UTF-8
 * bytes are written, so both cases end up with the same encoding.
 */
static void win32_console_utf8_init(void)
{
	HANDLE h = GetStdHandle(STD_OUTPUT_HANDLE);
	DWORD mode;

	if (h == NULL || h == INVALID_HANDLE_VALUE || !GetConsoleMode(h, &mode)) {
		return;
	}
	win32_console_cp_saved = GetConsoleOutputCP();
	if (win32_console_cp_saved != CP_UTF8 && SetConsoleOutputCP(CP_UTF8)) {
		win32_console_cp_changed = 1;
		atexit(win32_console_utf8_restore);
	}
}

typedef struct _stat64 path_stat_t;

static int path_stat_utf8(const char *path, path_stat_t *st)
{
	wchar_t *wpath = path_utf8_to_win32(path);
	int res = -1;

	if (!wpath) {
		errno = ENOENT;
		return -1;
	}
	/* _wstat64 and GetFileAttributesExW both refuse the \\?\UNC\ form on
	 * network paths, so use GetFileAttributesW (which handles every path
	 * form) and CreateFileW for the size, then fill in the CRT stat struct
	 * the callers expect. */
	{
		DWORD attr = GetFileAttributesW(wpath);
		if (attr == INVALID_FILE_ATTRIBUTES) {
			DWORD e = GetLastError();
			errno = (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND || e == ERROR_BAD_NETPATH) ? ENOENT : EACCES;
		} else {
			int64_t size = 0;
			if (!(attr & FILE_ATTRIBUTE_DIRECTORY)) {
				HANDLE h = CreateFileW(wpath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
					NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
				if (h == INVALID_HANDLE_VALUE) {
					/* The path exists but the size cannot be read (e.g. the file
					 * is locked without read sharing). Report the failure instead
					 * of claiming a zero byte file. */
					DWORD e = GetLastError();
					errno = (e == ERROR_SHARING_VIOLATION || e == ERROR_LOCK_VIOLATION) ? EBUSY : EACCES;
					free(wpath);
					return -1;
				}
				{
					LARGE_INTEGER li;
					if (GetFileSizeEx(h, &li)) {
						size = (int64_t)li.QuadPart;
					}
					CloseHandle(h);
				}
			}
			memset(st, 0, sizeof(*st));
			st->st_mode = (attr & FILE_ATTRIBUTE_DIRECTORY) ? (_S_IFDIR | _S_IEXEC) : _S_IFREG;
			st->st_nlink = 1;
			st->st_size = size;
			res = 0;
		}
	}
	free(wpath);
	return res;
}

/*
 * Open a file for reading through raw Win32 I/O, independent of MAX_PATH and
 * of the CRT's path handling; the CRT file descriptor is created on top of the
 * OS handle.  Used for reading archive and metadata files that may live in
 * very long paths.
 */
static FILE *path_fopen_read_utf8(const char *path)
{
	wchar_t *wpath = path_utf8_to_win32(path);
	HANDLE h;
	int fd;
	FILE *fp;

	if (!wpath) {
		errno = EINVAL;
		return NULL;
	}
	h = CreateFileW(wpath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
		NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL | FILE_FLAG_SEQUENTIAL_SCAN, NULL);
	free(wpath);
	if (h == INVALID_HANDLE_VALUE) {
		DWORD e = GetLastError();
		errno = (e == ERROR_FILE_NOT_FOUND) ? ENOENT : (e == ERROR_PATH_NOT_FOUND) ? ENOENT :
			(e == ERROR_SHARING_VIOLATION || e == ERROR_LOCK_VIOLATION) ? EBUSY : EACCES;
		return NULL;
	}
	fd = _open_osfhandle((intptr_t)h, _O_RDONLY | _O_BINARY);
	if (fd < 0) {
		CloseHandle(h);
		errno = EMFILE;
		return NULL;
	}
	fp = _fdopen(fd, "rb");
	if (!fp) {
		_close(fd);
		errno = ENOMEM;
		return NULL;
	}
	return fp;
}

static FILE *path_fopen_utf8(const char *path, const char *mode)
{
	wchar_t *wpath = path_utf8_to_win32(path);
	wchar_t *wmode = utf8_to_wide(mode);
	FILE *fp;

	if (!wpath || !wmode) {
		free(wpath);
		free(wmode);
		errno = EINVAL;
		return NULL;
	}
	fp = _wfopen(wpath, wmode);
	free(wpath);
	free(wmode);
	return fp;
}

/*
 * Open an archive through a Win32 file handle so that libzip never has to
 * resolve a path itself: 255 character components and paths above MAX_PATH,
 * UNC paths and \\?\ prefixed paths all work.  The handle is taken over by
 * libzip (which closes it through zip_fdopen/zip_source_filep semantics).
 */
static struct zip *zip_open_path_utf8(const char *path, int *errp)
{
	wchar_t *wpath = path_utf8_to_win32(path);
	struct zip *zf = NULL;
	HANDLE h;
	int fd;

	if (errp) {
		*errp = ZIP_ER_OPEN;
	}
	if (!wpath) {
		return NULL;
	}
	h = CreateFileW(wpath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
		NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	free(wpath);
	if (h == INVALID_HANDLE_VALUE) {
		DWORD e = GetLastError();
		/* ZIP_ER_NOENT / ZIP_ER_OPEN are libzip's own error codes (not errno) */
		if (errp) {
			*errp = (e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND) ? ZIP_ER_NOENT : ZIP_ER_OPEN;
		}
		return NULL;
	}
	fd = _open_osfhandle((intptr_t)h, _O_RDONLY | _O_BINARY);
	if (fd < 0) {
		CloseHandle(h);
		return NULL;
	}
	errno = 0;
	zf = zip_fdopen(fd, 0, errp);
	if (!zf) {
		_close(fd);
	}
	return zf;
}
#else
#define wait_ms(x) { struct timespec ts; ts.tv_sec = 0; ts.tv_nsec = x * 1000000; nanosleep(&ts, NULL); }

typedef struct stat path_stat_t;

static int path_stat_utf8(const char *path, path_stat_t *st)
{
	return stat(path, st);
}

static FILE *path_fopen_utf8(const char *path, const char *mode)
{
	return fopen(path, mode);
}

static FILE *path_fopen_read_utf8(const char *path)
{
	return fopen(path, "rb");
}

static struct zip *zip_open_path_utf8(const char *path, int *errp)
{
	return zip_open(path, 0, errp);
}
#endif

#ifndef HAVE_VASPRINTF
static int vasprintf(char **PTR, const char *TEMPLATE, va_list AP)
{
	int res;
	char buf[16];
	res = vsnprintf(buf, 16, TEMPLATE, AP);
	if (res > 0) {
		*PTR = (char*)malloc(res+1);
		res = vsnprintf(*PTR, res+1, TEMPLATE, AP);
	}
	return res;
}
#endif

#ifndef HAVE_ASPRINTF
static int asprintf(char **PTR, const char *TEMPLATE, ...)
{
	int res;
	va_list AP;
	va_start(AP, TEMPLATE);
	res = vasprintf(PTR, TEMPLATE, AP);
	va_end(AP);
	return res;
}
#endif

#define ITUNES_METADATA_PLIST_FILENAME "iTunesMetadata.plist"

const char PKG_PATH[] = "PublicStaging";
const char APPARCH_PATH[] = "ApplicationArchives";

char *udid = NULL;
char *cmdarg = NULL;
char *extsinf = NULL;
char *extmeta = NULL;

enum cmd_mode {
	CMD_NONE = 0,
	CMD_LIST_APPS,
	CMD_INSTALL,
	CMD_UNINSTALL,
	CMD_UPGRADE,
	CMD_LIST_ARCHIVES,
	CMD_ARCHIVE,
	CMD_RESTORE,
	CMD_REMOVE_ARCHIVE
};

int cmd = CMD_NONE;

char *last_status = NULL;
int wait_for_command_complete = 0;
int use_network = 0;
int use_notifier = 0;
int notification_expected = 0;
int is_device_connected = 0;
int command_completed = 0;
int ignore_events = 0;
int err_occurred = 0;
int notified = 0;
plist_t bundle_ids = NULL;
plist_t return_attrs = NULL;
#define FORMAT_XML 1
#define FORMAT_JSON 2
int output_format = 0;
int opt_list_user = 0;
int opt_list_system = 0;
char *copy_path = NULL;
int remove_after_copy = 0;
int skip_uninstall = 1;
int app_only = 0;
int docs_only = 0;

static void print_apps_header()
{
	if (!return_attrs) {
		return;
	}
	uint32_t i = 0;
	for (i = 0; i < plist_array_get_size(return_attrs); i++) {
		plist_t node = plist_array_get_item(return_attrs, i);
		if (i > 0) {
			printf(", ");
		}
		printf("%s", plist_get_string_ptr(node, NULL));
	}
	printf("\n");
}

static void print_apps(plist_t apps)
{
	if (!return_attrs) {
		return;
	}
	uint32_t i = 0;
	for (i = 0; i < plist_array_get_size(apps); i++) {
		plist_t app = plist_array_get_item(apps, i);
		uint32_t j = 0;
		for (j = 0; j < plist_array_get_size(return_attrs); j++) {
			plist_t node = plist_array_get_item(return_attrs, j);
			if (j > 0) {
				printf(", ");
			}
			const char* key = plist_get_string_ptr(node, NULL);
			node = plist_dict_get_item(app, key);
			if (node) {
				if (!strcmp(key, "CFBundleIdentifier")) {
					printf("%s", plist_get_string_ptr(node, NULL));
				} else {
					plist_write_to_stream(node, stdout, PLIST_FORMAT_PRINT, PLIST_OPT_NO_NEWLINE);
				}
			}
		}
		printf("\n");
	}
}

static void notifier(const char *notification, void *unused)
{
	notified = 1;
}

static void status_cb(plist_t command, plist_t status, void *unused)
{
	if (command && status) {
		char* command_name = NULL;
		instproxy_command_get_name(command, &command_name);

		/* get status */
		char *status_name = NULL;
		instproxy_status_get_name(status, &status_name);

		if (status_name) {
			if (!strcmp(status_name, "Complete")) {
				command_completed = 1;
			}
		}

		/* get error if any */
		char* error_name = NULL;
		char* error_description = NULL;
		uint64_t error_code = 0;
		instproxy_status_get_error(status, &error_name, &error_description, &error_code);

		/* output/handling */
		if (!error_name) {
			if (!strcmp(command_name, "Browse")) {
				uint64_t total = 0;
				uint64_t current_index = 0;
				uint64_t current_amount = 0;
				plist_t current_list = NULL;
				instproxy_status_get_current_list(status, &total, &current_index, &current_amount, &current_list);
				if (current_list) {
					print_apps(current_list);
					plist_free(current_list);
				}
			} else if (status_name) {
				/* get progress if any */
				int percent = -1;
				instproxy_status_get_percent_complete(status, &percent);

				if (last_status && (strcmp(last_status, status_name))) {
					printf("\n");
				}

				if (percent >= 0) {
					printf("%s: %s (%d%%)\n", command_name, status_name, percent);
				} else {
					printf("%s: %s\n", command_name, status_name);
				}
				/* keep a GUI reading the pipe informed while the command runs */
				fflush(stdout);
			}
		} else {
			/* report error to the user */
			if (error_description)
				fprintf(stderr, "ERROR: %s failed. Got error \"%s\" with code 0x%08"PRIx64": %s\n", command_name, error_name, error_code, error_description ? error_description: "N/A");
			else
				fprintf(stderr, "ERROR: %s failed. Got error \"%s\".\n", command_name, error_name);
			err_occurred = 1;
		}

		/* clean up */
		free(error_name);
		free(error_description);

		free(last_status);
		last_status = status_name;

		free(command_name);
		command_name = NULL;
	} else {
		fprintf(stderr, "ERROR: %s was called with invalid arguments!\n", __func__);
	}
}

static int zip_get_contents(struct zip *zf, const char *filename, int locate_flags, char **buffer, uint32_t *len)
{
	struct zip_stat zs;
	struct zip_file *zfile;
	int zindex = zip_name_locate(zf, filename, locate_flags);

	*buffer = NULL;
	*len = 0;

	if (zindex < 0) {
		return -1;
	}

	zip_stat_init(&zs);

	if (zip_stat_index(zf, zindex, 0, &zs) != 0) {
		fprintf(stderr, "ERROR: zip_stat_index '%s' failed!\n", filename);
		return -2;
	}

	if (zs.size > 10485760) {
		fprintf(stderr, "ERROR: file '%s' is too large!\n", filename);
		return -3;
	}

	zfile = zip_fopen_index(zf, zindex, 0);
	if (!zfile) {
		fprintf(stderr, "ERROR: zip_fopen '%s' failed!\n", filename);
		return -4;
	}

	*buffer = malloc(zs.size);
	if (zs.size > LLONG_MAX || zip_fread(zfile, *buffer, zs.size) != (zip_int64_t)zs.size) {
		fprintf(stderr, "ERROR: zip_fread %" PRIu64 " bytes from '%s'\n", (uint64_t)zs.size, filename);
		free(*buffer);
		*buffer = NULL;
		zip_fclose(zfile);
		return -5;
	}
	*len = zs.size;
	zip_fclose(zfile);
	return 0;
}

static int zip_get_app_directory(struct zip* zf, char** path)
{
	zip_int64_t i = 0;
	zip_int64_t c = (zip_int64_t)zip_get_num_entries(zf, 0);
	int len = 0;
	const char* name = NULL;

	/* look through all filenames in the archive */
	do {
		/* get filename at current index */
		name = zip_get_name(zf, i++, 0);
		if (name != NULL) {
			/* check if we have a "Payload/.../" name */
			len = strlen(name);
			if (!strncmp(name, "Payload/", 8) && (len > 8)) {
				/* skip hidden files */
				if (name[8] == '.')
					continue;

				/* locate the second directory delimiter */
				const char* p = name + 8;
				do {
					if (*p == '/') {
						break;
					}
				} while(p++ != NULL);

				/* try next entry if not found */
				if (p == NULL)
					continue;

				len = p - name + 1;

				/* make sure app directory endwith .app */
				if (len < 12 || strncmp(p - 4, ".app", 4))
				{
					continue;
				}

				if (path != NULL) {
					free(*path);
					*path = NULL;
				}

				/* allocate and copy filename */
				*path = (char*)malloc(len + 1);
				strncpy(*path, name, len);

				/* add terminating null character */
				char* t = *path + len;
				*t = '\0';
				break;
			}
		}
	} while(i < c);

	if (*path == NULL) {
		return -1;
	}

	return 0;
}

static void idevice_event_callback(const idevice_event_t* event, void* userdata)
{
	if (ignore_events) {
		return;
	}
	if (event->event == IDEVICE_DEVICE_REMOVE) {
		if (!strcmp(udid, event->udid)) {
			fprintf(stderr, "ideviceinstaller: Device removed\n");
			is_device_connected = 0;
		}
	}
}

static void idevice_wait_for_command_to_complete()
{
	is_device_connected = 1;
	ignore_events = 0;

	/* subscribe to make sure to exit on device removal */
	idevice_event_subscribe(idevice_event_callback, NULL);

	/* wait for command to complete */
	while (wait_for_command_complete && !command_completed && !err_occurred
		   && is_device_connected) {
		wait_ms(50);
	}

	/* wait some time if a notification is expected */
	while (use_notifier && notification_expected && !notified && !err_occurred && is_device_connected) {
		wait_ms(50);
	}

	ignore_events = 1;
	idevice_event_unsubscribe();
}

static void print_usage(int argc, char **argv, int is_error)
{
	char *name = strrchr(argv[0], '/');
	fprintf((is_error) ? stderr : stdout, "Usage: %s OPTIONS\n", (name ? name + 1 : argv[0]));
	fprintf((is_error) ? stderr : stdout,
	"\n"
	"Manage apps on iOS devices.\n"
	"\n"
	"COMMANDS:\n"
	"  list                List installed apps. Options:\n"
	"        --user          List user apps only (this is the default)\n"
	"        --system        List system apps only\n"
	"        --all           List all types of apps\n"
	"        --xml           Print output as XML Property List\n"
	"        -a, --attribute ATTR  Specify attribute to return - see man page\n"
	"            (can be passed multiple times)\n"
	"        -b, --bundle-identifier BUNDLEID  Only query given bundle identifier\n"
	"            (can be passed multiple times)\n"
	"  install PATH        Install app from package file specified by PATH.\n"
	"                      PATH can also be a .ipcc file for carrier bundles.\n"
	"        -s, --sinf PATH  Pass an external SINF file\n"
	"        -m, --metadata PATH  Pass an external iTunesMetadata file\n"
	"  uninstall BUNDLEID  Uninstall app specified by BUNDLEID.\n"
	"  upgrade PATH        Upgrade app from package file specified by PATH.\n"
        "\n"
        "LEGACY COMMANDS (non-functional with iOS 7 or later):\n"
	"  archive BUNDLEID    Archive app specified by BUNDLEID. Options:\n"
	"        --uninstall     Uninstall the package after making an archive\n"
	"        --app-only      Archive application data only\n"
	"        --docs-only     Archive documents (user data) only\n"
	"        --copy=PATH     Copy the app archive to directory PATH when done\n"
	"        --remove        Only valid when copy=PATH is used: remove after copy\n"
	"  restore BUNDLEID    Restore archived app specified by BUNDLEID\n"
	"  list-archives       List archived apps. Options:\n"
	"        --xml           Print output as XML Property List\n"
	"  remove-archive BUNDLEID    Remove app archive specified by BUNDLEID\n"
	"\n"
	"OPTIONS:\n"
	"  -u, --udid UDID     Target specific device by UDID\n"
	"  -n, --network       Connect to network device\n"
	"  -w, --notify-wait   Wait for app installed/uninstalled notification\n"
	"                      before reporting success of operation\n"
	"  -h, --help          Print usage information\n"
	"  -d, --debug         Enable communication debugging\n"
	"  -v, --version       Print version information\n"
	"\n"
	"Homepage:    <" PACKAGE_URL ">\n"
	"Bug Reports: <" PACKAGE_BUGREPORT ">\n"
	);
}

enum numerical_opts {
	LIST_USER = 1,
	LIST_SYSTEM,
	LIST_ALL,
	ARCHIVE_UNINSTALL,
	ARCHIVE_APP_ONLY,
	ARCHIVE_DOCS_ONLY,
	ARCHIVE_COPY_PATH,
	ARCHIVE_COPY_REMOVE,
	OUTPUT_XML,
	OUTPUT_JSON
};

static void parse_opts(int argc, char **argv)
{
	static struct option longopts[] = {
		{ "help", no_argument, NULL, 'h' },
		{ "udid", required_argument, NULL, 'u' },
		{ "network", no_argument, NULL, 'n' },
		{ "notify-wait", no_argument, NULL, 'w' },
		{ "debug", no_argument, NULL, 'd' },
		{ "version", no_argument, NULL, 'v' },
		{ "bundle-identifier", required_argument, NULL, 'b' },
		{ "attribute", required_argument, NULL, 'a' },
		{ "user", no_argument, NULL, LIST_USER },
		{ "system", no_argument, NULL, LIST_SYSTEM },
		{ "all", no_argument, NULL, LIST_ALL },
		{ "xml", no_argument, NULL, OUTPUT_XML },
		{ "json", no_argument, NULL, OUTPUT_JSON },
		{ "sinf", required_argument, NULL, 's' },
		{ "metadata", required_argument, NULL, 'm' },
		{ "uninstall", no_argument, NULL, ARCHIVE_UNINSTALL },
		{ "app-only", no_argument, NULL, ARCHIVE_APP_ONLY },
		{ "docs-only", no_argument, NULL, ARCHIVE_DOCS_ONLY },
		{ "copy", required_argument, NULL, ARCHIVE_COPY_PATH },
		{ "remove", no_argument, NULL, ARCHIVE_COPY_REMOVE },
		{ NULL, 0, NULL, 0 }
	};
	int c;

	while (1) {
		c = getopt_long(argc, argv, "hu:nwdvb:a:s:m:", longopts, (int*)0);
		if (c == -1) {
			break;
		}

		switch (c) {
		case 'h':
			print_usage(argc, argv, 0);
			exit(0);
		case 'u':
			if (!*optarg) {
				printf("ERROR: UDID must not be empty!\n");
				print_usage(argc, argv, 1);
				exit(2);
			}
			udid = strdup(optarg);
			break;
		case 'n':
			use_network = 1;
			break;
		case 'a':
			if (!*optarg) {
				printf("ERROR: attribute must not be empty!\n");
				print_usage(argc, argv, 1);
				exit(2);
			}
			if (return_attrs == NULL) {
				return_attrs = plist_new_array();
			}
			plist_array_append_item(return_attrs, plist_new_string(optarg));
			break;
		case 'b':
			if (!*optarg) {
				printf("ERROR: bundle identifier must not be empty!\n");
				print_usage(argc, argv, 1);
				exit(2);
			}
			if (bundle_ids == NULL) {
				bundle_ids = plist_new_array();
			}
			plist_array_append_item(bundle_ids, plist_new_string(optarg));
			break;
		case 's':
			if (!*optarg) {
				printf("ERROR: path for --sinf must not be empty!\n");
				print_usage(argc, argv, 1);
				exit(2);
			}
			extsinf = strdup(optarg);
			break;
		case 'm':
			if (!*optarg) {
				printf("ERROR: path for --metadata must not be empty!\n");
				print_usage(argc, argv, 1);
				exit(2);
			}
			extmeta = strdup(optarg);
			break;
		case 'w':
			use_notifier = 1;
			break;
		case 'd':
			idevice_set_debug_level(1);
			break;
		case 'v':
			printf("%s %s\n", PACKAGE_NAME, PACKAGE_VERSION);
			exit(0);
		case LIST_USER:
			opt_list_user = 1;
			break;
		case LIST_SYSTEM:
			opt_list_system = 1;
			break;
		case LIST_ALL:
			opt_list_user = 1;
			opt_list_system = 1;
			break;
		case OUTPUT_XML:
			output_format = FORMAT_XML;
			break;
		case OUTPUT_JSON:
			output_format = FORMAT_JSON;
			break;
		case ARCHIVE_UNINSTALL:
			skip_uninstall = 0;
			break;
		case ARCHIVE_APP_ONLY:
			app_only = 1;
			docs_only = 0;
			break;
		case ARCHIVE_DOCS_ONLY:
			docs_only = 1;
			app_only = 0;
			break;
		case ARCHIVE_COPY_PATH:
			copy_path = strdup(optarg);
			break;
		case ARCHIVE_COPY_REMOVE:
			remove_after_copy = 1;
			break;
		default:
			print_usage(argc, argv, 1);
			exit(2);
		}
	}

        argv += optind;
	argc -= optind;

	if (argc == 0) {
		fprintf(stderr, "ERROR: Missing command.\n\n");
		print_usage(argc+optind, argv-optind, 1);
		exit(2);
	}

	char *cmdstr = argv[0];

	if (!strcmp(cmdstr, "list")) {
		cmd = CMD_LIST_APPS;
	} else if (!strcmp(cmdstr, "install")) {
		cmd = CMD_INSTALL;
	} else if (!strcmp(cmdstr, "upgrade")) {
		cmd = CMD_UPGRADE;
	} else if (!strcmp(cmdstr, "uninstall") || !strcmp(cmdstr, "remove")) {
		cmd = CMD_UNINSTALL;
	} else if (!strcmp(cmdstr, "archives") || !strcmp(cmdstr, "list-archives")) {
		cmd = CMD_LIST_ARCHIVES;
	} else if (!strcmp(cmdstr, "archive")) {
		cmd = CMD_ARCHIVE;
	} else if (!strcmp(cmdstr, "restore")) {
		cmd = CMD_RESTORE;
	} else if (!strcmp(cmdstr, "remove-archive")) {
		cmd = CMD_REMOVE_ARCHIVE;
	}

	switch (cmd) {
		case CMD_LIST_APPS:
		case CMD_LIST_ARCHIVES:
			break;
		case CMD_INSTALL:
		case CMD_UPGRADE:
			if (argc < 2) {
				fprintf(stderr, "ERROR: Missing filename for '%s' command.\n\n", cmdstr);
				print_usage(argc+optind, argv-optind, 1);
				exit(2);
			}
			cmdarg = argv[1];
			break;
		case CMD_UNINSTALL:
		case CMD_ARCHIVE:
		case CMD_RESTORE:
		case CMD_REMOVE_ARCHIVE:
			if (argc < 2) {
				fprintf(stderr, "ERROR: Missing bundle ID for '%s' command.\n\n", cmdstr);
				print_usage(argc+optind, argv-optind, 1);
				exit(2);
			}
			cmdarg = argv[1];
			break;
		default:
			fprintf(stderr, "ERROR: Invalid command '%s'.\n\n", cmdstr);
			print_usage(argc+optind, argv-optind, 1);
			exit(2);
	}
}

static double now_seconds(void)
{
#ifdef WIN32
	static LARGE_INTEGER freq;
	LARGE_INTEGER now;
	if (freq.QuadPart == 0) {
		QueryPerformanceFrequency(&freq);
	}
	QueryPerformanceCounter(&now);
	return (double)now.QuadPart / (double)freq.QuadPart;
#else
	struct timespec ts;
	clock_gettime(CLOCK_MONOTONIC, &ts);
	return (double)ts.tv_sec + (double)ts.tv_nsec / 1e9;
#endif
}

/* Total size of the file behind a freshly opened stdio stream, in bytes; the
 * stream is rewound afterwards.  Returns -1 when it cannot be determined.
 *
 * Plain fseeko()/ftello() must not be used for this: on MinGW the default
 * off_t is 32 bit, so ftello() overflows and returns -1 for files above
 * 2 GiB even though the seek itself succeeds - which used to drop the whole
 * transfer progress output silently for large packages.  POSIX builds should
 * define _FILE_OFFSET_BITS=64 (AC_SYS_LARGEFILE) for the same reason. */
static int64_t file_size_and_rewind(FILE *f)
{
#ifdef WIN32
	__int64 end;

	if (_fseeki64(f, 0, SEEK_END) != 0) {
		return -1;
	}
	end = _ftelli64(f);
	rewind(f);
	return (end < 0) ? -1 : (int64_t)end;
#else
	off_t end;

	if (fseeko(f, 0, SEEK_END) != 0) {
		return -1;
	}
	end = ftello(f);
	rewind(f);
	return (end < 0) ? -1 : (int64_t)end;
#endif
}

static int afc_upload_file(afc_client_t afc, const char* filename, const char* dstfn)
{
	FILE *f = NULL;
	uint64_t af = 0;
	char buf[1048576];

	f = path_fopen_utf8(filename, "rb");
	if (!f) {
		fprintf(stderr, "fopen: %s: %s\n", filename, strerror(errno));
		return -1;
	}

	if ((afc_file_open(afc, dstfn, AFC_FOPEN_WRONLY, &af) != AFC_E_SUCCESS) || !af) {
		fclose(f);
		fprintf(stderr, "afc_file_open on '%s' failed!\n", dstfn);
		return -1;
	}

	/* transfer progress: one line per 5% step, MiB/s, newline separated so a
	 * GUI reading the pipe can parse it without handling carriage returns */
	uint64_t file_size = 0;
	uint64_t sent = 0;
	int last_bucket = -1;
	double t_start = 0.0;
	int64_t fsize;

	fsize = file_size_and_rewind(f);
	if (fsize > 0) {
		file_size = (uint64_t)fsize;
	}
	t_start = now_seconds();

	size_t amount = 0;
	do {
		amount = fread(buf, 1, sizeof(buf), f);
		if (amount > 0) {
			uint32_t written, total = 0;
			while (total < amount) {
				written = 0;
				afc_error_t aerr = afc_file_write(afc, af, buf, amount, &written);
				if (aerr != AFC_E_SUCCESS) {
					fprintf(stderr, "AFC Write error: %d\n", aerr);
					break;
				}
				total += written;
			}
			if (total != amount) {
				fprintf(stderr, "Error: wrote only %u of %u\n", total, (uint32_t)amount);
				afc_file_close(afc, af);
				fclose(f);
				return -1;
			}
			sent += amount;
			if (file_size > 0) {
				int bucket = (int)((sent * 100) / file_size);
				bucket = (bucket / 5) * 5;
				if (bucket > 100) {
					bucket = 100;
				}
				if (bucket > last_bucket) {
					double elapsed = now_seconds() - t_start;
					double mib = (double)sent / (1024.0 * 1024.0);
					double rate = (elapsed > 0.001) ? (mib / elapsed) : 0.0;
					printf("  Transfer: %3d%%  (%6.2f MiB/s)\n", bucket, rate);
					fflush(stdout);
					last_bucket = bucket;
				}
			}
		}
	} while (amount > 0);

	{
		double elapsed = now_seconds() - t_start;
		double mib = (double)sent / (1024.0 * 1024.0);
		double rate = (elapsed > 0.001) ? (mib / elapsed) : 0.0;
		printf("  Transferred %.2f MiB in %.2fs (%.2f MiB/s)\n", mib, elapsed, rate);
		fflush(stdout);
	}

	afc_file_close(afc, af);
	fclose(f);

	return 0;
}

#ifdef WIN32
static void afc_upload_dir(afc_client_t afc, const char* path, const char* afcpath)
{
	wchar_t *wpath;
	wchar_t *wpattern;
	WIN32_FIND_DATAW fd;
	HANDLE h;
	size_t len;

	afc_make_directory(afc, afcpath);

	path_stat_t st;
	if (path_stat_utf8(path, &st) != 0 || !S_ISDIR(st.st_mode)) {
		fprintf(stderr, "fopen: %s: %s\n", path, "Not a directory");
		return;
	}

	wpath = path_utf8_to_win32(path);
	if (!wpath) {
		return;
	}
	len = wcslen(wpath);
	/* strip one trailing backslash (but keep "C:\" and "\\?\C:\") */
	if (len > 3 && wpath[len - 1] == L'\\' && wpath[len - 2] != L':') {
		wpath[--len] = L'\0';
	}
	wpattern = (wchar_t*)malloc((len + 3) * sizeof(wchar_t));
	if (!wpattern) {
		free(wpath);
		return;
	}
	memcpy(wpattern, wpath, len * sizeof(wchar_t));
	wpattern[len] = L'\\';
	wpattern[len + 1] = L'*';
	wpattern[len + 2] = L'\0';
	free(wpath);
	h = FindFirstFileW(wpattern, &fd);
	free(wpattern);
	if (h == INVALID_HANDLE_VALUE) {
		fprintf(stderr, "fopen: %s: %s\n", path, (GetLastError() == ERROR_FILE_NOT_FOUND) ? "No such file or directory" : "cannot enumerate directory");
		return;
	}
	do {
		char *name;
		char *fpath = NULL;
		char *apath = NULL;

		if ((wcscmp(fd.cFileName, L".") == 0) || (wcscmp(fd.cFileName, L"..") == 0)) {
			continue;
		}
		name = wide_to_utf8(fd.cFileName);
		if (!name) {
			continue;
		}
		if (asprintf(&fpath, "%s/%s", path, name) < 0) {
			fpath = NULL;
		}
		if (asprintf(&apath, "%s/%s", afcpath, name) < 0) {
			apath = NULL;
		}
		if (fpath && apath) {
			if (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) {
				/* Symlinks / directory junctions are not followed: they can
				 * form cycles or duplicate the whole tree. */
				fprintf(stderr, "WARNING: skipping reparse point '%s'\n", fpath);
			} else if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) {
				afc_upload_dir(afc, fpath, apath);
			} else {
				afc_upload_file(afc, fpath, apath);
			}
		}
		free(fpath);
		free(apath);
		free(name);
	} while (FindNextFileW(h, &fd));
	FindClose(h);
}
#else
static void afc_upload_dir(afc_client_t afc, const char* path, const char* afcpath)
{
	afc_make_directory(afc, afcpath);

	DIR *dir = opendir(path);
	if (dir) {
		struct dirent* ep;
		while ((ep = readdir(dir))) {
			if ((strcmp(ep->d_name, ".") == 0) || (strcmp(ep->d_name, "..") == 0)) {
				continue;
			}
			char *fpath = (char*)malloc(strlen(path)+1+strlen(ep->d_name)+1);
			char *apath = (char*)malloc(strlen(afcpath)+1+strlen(ep->d_name)+1);

			struct stat st;

			strcpy(fpath, path);
			strcat(fpath, "/");
			strcat(fpath, ep->d_name);

			strcpy(apath, afcpath);
			strcat(apath, "/");
			strcat(apath, ep->d_name);

#ifdef HAVE_LSTAT
			if ((lstat(fpath, &st) == 0) && S_ISLNK(st.st_mode)) {
				char *target = (char *)malloc(st.st_size+1);
				if (readlink(fpath, target, st.st_size+1) < 0) {
					fprintf(stderr, "ERROR: readlink: %s (%d)\n", strerror(errno), errno);
				} else {
					target[st.st_size] = '\0';
					afc_make_link(afc, AFC_SYMLINK, target, fpath);
				}
				free(target);
			} else
#endif
			if ((stat(fpath, &st) == 0) && S_ISDIR(st.st_mode)) {
				afc_upload_dir(afc, fpath, apath);
			} else {
				afc_upload_file(afc, fpath, apath);
			}
			free(fpath);
			free(apath);
		}
		closedir(dir);
	}
}
#endif

static char *buf_from_file(const char *filename, size_t *size)
{
	path_stat_t st;
	FILE *fp = NULL;

	if (path_stat_utf8(filename, &st) == -1 || (fp = path_fopen_read_utf8(filename)) == NULL) {
		return NULL;
	}
	size_t filesize = (size_t)st.st_size;
	if (filesize == 0) {
		return NULL;
	}
	char *ibuf = malloc(filesize * sizeof(char));
	if (ibuf == NULL) {
		return NULL;
	}
	size_t amount = fread(ibuf, 1, filesize, fp);
	if (amount != filesize) {
		fprintf(stderr, "ERROR: could not read %" PRIu64 " bytes from %s\n", (uint64_t)filesize, filename);
		free(ibuf);
		return NULL;
	}
	fclose(fp);

	if (size) {
		*size = filesize;
	}

	return ibuf;
}

int main(int argc, char **argv)
{
	idevice_t device = NULL;
	lockdownd_client_t client = NULL;
	instproxy_client_t ipc = NULL;
	instproxy_error_t err;
	np_client_t np = NULL;
	afc_client_t afc = NULL;
	lockdownd_service_descriptor_t service = NULL;
	int res = EXIT_FAILURE;
	char *bundleidentifier = NULL;

#ifdef WIN32
	win32_argv_to_utf8(&argc, &argv);
	win32_console_utf8_init();
#endif
#ifndef WIN32
	signal(SIGPIPE, SIG_IGN);
#endif
	parse_opts(argc, argv);

	argc -= optind;
	argv += optind;

	if (IDEVICE_E_SUCCESS != idevice_new_with_options(&device, udid, (use_network) ? IDEVICE_LOOKUP_NETWORK : IDEVICE_LOOKUP_USBMUX)) {
		if (udid) {
			fprintf(stderr, "No device found with udid %s.\n", udid);
		} else {
			fprintf(stderr, "No device found.\n");
		}
		return EXIT_FAILURE;
	}

	if (!udid) {
		idevice_get_udid(device, &udid);
	}

	lockdownd_error_t lerr = lockdownd_client_new_with_handshake(device, &client, "ideviceinstaller");
	if (lerr != LOCKDOWN_E_SUCCESS) {
		fprintf(stderr, "Could not connect to lockdownd: %s. Exiting.\n", lockdownd_strerror(lerr));
		goto leave_cleanup;
	}

	if (use_notifier) {
		lerr =lockdownd_start_service(client, "com.apple.mobile.notification_proxy", &service);
		if (lerr != LOCKDOWN_E_SUCCESS) {
			fprintf(stderr,	"Could not start com.apple.mobile.notification_proxy: %s\n", lockdownd_strerror(lerr));
			goto leave_cleanup;
		}

		np_error_t nperr = np_client_new(device, service, &np);

		if (service) {
			lockdownd_service_descriptor_free(service);
		}
		service = NULL;

		if (nperr != NP_E_SUCCESS) {
			fprintf(stderr, "Could not connect to notification_proxy!\n");
			goto leave_cleanup;
		}

		np_set_notify_callback(np, notifier, NULL);

		const char *noties[3] = { NP_APP_INSTALLED, NP_APP_UNINSTALLED, NULL };

		np_observe_notifications(np, noties);
	}

run_again:
	if (service) {
		lockdownd_service_descriptor_free(service);
	}
	service = NULL;

	lerr = lockdownd_start_service(client, "com.apple.mobile.installation_proxy", &service);
	if (lerr != LOCKDOWN_E_SUCCESS) {
		fprintf(stderr, "Could not start com.apple.mobile.installation_proxy: %s\n", lockdownd_strerror(lerr));
		goto leave_cleanup;
	}

	err = instproxy_client_new(device, service, &ipc);

	if (service) {
		lockdownd_service_descriptor_free(service);
	}
	service = NULL;

	if (err != INSTPROXY_E_SUCCESS) {
		fprintf(stderr, "Could not connect to installation_proxy!\n");
		goto leave_cleanup;
	}

	setbuf(stdout, NULL);

	free(last_status);
	last_status = NULL;

	notification_expected = 0;

	if (cmd == CMD_LIST_APPS) {
		plist_t client_opts = instproxy_client_options_new();
		instproxy_client_options_add(client_opts, "ApplicationType", "User", NULL);
		plist_t apps = NULL;

		if (opt_list_system && opt_list_user) {
			plist_dict_remove_item(client_opts, "ApplicationType");
		} else if (opt_list_system) {
			instproxy_client_options_add(client_opts, "ApplicationType", "System", NULL);
		} else if (opt_list_user) {
			instproxy_client_options_add(client_opts, "ApplicationType", "User", NULL);
		}

		if (bundle_ids) {
			plist_dict_set_item(client_opts, "BundleIDs", plist_copy(bundle_ids));
		}

		if (!output_format && !return_attrs) {
			return_attrs = plist_new_array();
			plist_array_append_item(return_attrs, plist_new_string("CFBundleIdentifier"));
			plist_array_append_item(return_attrs, plist_new_string("CFBundleShortVersionString"));
			plist_array_append_item(return_attrs, plist_new_string("CFBundleDisplayName"));
		}

		if (return_attrs) {
			instproxy_client_options_add(client_opts, "ReturnAttributes", return_attrs, NULL);
		}

		if (output_format) {
			err = instproxy_browse(ipc, client_opts, &apps);

			if (!apps || (plist_get_node_type(apps) != PLIST_ARRAY)) {
				fprintf(stderr, "ERROR: instproxy_browse returnd an invalid plist!\n");
				goto leave_cleanup;
			}
			char *buf = NULL;
			uint32_t len = 0;
			if (output_format == FORMAT_XML) {
				plist_err_t perr = plist_to_xml(apps, &buf, &len);
				if (perr != PLIST_ERR_SUCCESS) {
					fprintf(stderr, "ERROR: Failed to convert data to XML format (%d).\n", perr);
				}
			} else if (output_format == FORMAT_JSON) {
				/* for JSON, we need to convert some stuff since it doesn't support PLIST_DATA nodes */
				plist_array_iter aiter = NULL;
				plist_array_new_iter(apps, &aiter);
				plist_t entry = NULL;
				do {
					plist_array_next_item(apps, aiter, &entry);
					if (!entry) break;
					plist_t items = plist_dict_get_item(entry, "UIApplicationShortcutItems");
					plist_array_iter inner = NULL;
					plist_array_new_iter(items, &inner);
					plist_t item = NULL;
					do {
						plist_array_next_item(items, inner, &item);
						if (!item) break;
						plist_t userinfo = plist_dict_get_item(item, "UIApplicationShortcutItemUserInfo");
						if (userinfo) {
							plist_t data_node = plist_dict_get_item(userinfo, "data");

							if (data_node) {
								char *strbuf = NULL;
								uint32_t buflen = 0;
								plist_write_to_string(data_node, &strbuf, &buflen, PLIST_FORMAT_LIMD, PLIST_OPT_NO_NEWLINE);
								plist_set_string_val(data_node, strbuf);
								free(strbuf);
							}
						}
					} while (item);
					free(inner);
				} while (entry);
				free(aiter);
				plist_err_t perr = plist_to_json(apps, &buf, &len, 1);
				if (perr != PLIST_ERR_SUCCESS) {
					fprintf(stderr, "ERROR: Failed to convert data to JSON format (%d).\n", perr);
				}
			}
			if (buf) {
				puts(buf);
				free(buf);
			}
			plist_free(apps);
			res = 0;
			goto leave_cleanup;
		}

		print_apps_header();

		err = instproxy_browse_with_callback(ipc, client_opts, status_cb, NULL);
		if (err == INSTPROXY_E_RECEIVE_TIMEOUT) {
			fprintf(stderr, "NOTE: timeout waiting for device to browse apps, trying again...\n");
		}

		instproxy_client_options_free(client_opts);
		if (err != INSTPROXY_E_SUCCESS) {
			fprintf(stderr, "ERROR: instproxy_browse returned %d\n", err);
			goto leave_cleanup;
		}

		wait_for_command_complete = 1;
		notification_expected = 0;
	} else if (cmd == CMD_INSTALL || cmd == CMD_UPGRADE) {
		plist_t sinf = NULL;
		plist_t meta = NULL;
		char *pkgname = NULL;
		path_stat_t fst;
		uint64_t af = 0;
		char buf[8192];

		lockdownd_service_descriptor_free(service);
		service = NULL;

		lerr = lockdownd_start_service(client, "com.apple.afc", &service);
		if (lerr != LOCKDOWN_E_SUCCESS) {
			fprintf(stderr, "Could not start com.apple.afc: %s\n", lockdownd_strerror(lerr));
			goto leave_cleanup;
		}

		lockdownd_client_free(client);
		client = NULL;

		if (afc_client_new(device, service, &afc) != AFC_E_SUCCESS) {
			fprintf(stderr, "Could not connect to AFC!\n");
			goto leave_cleanup;
		}

		if (path_stat_utf8(cmdarg, &fst) != 0) {
			fprintf(stderr, "ERROR: stat: %s: %s\n", cmdarg, strerror(errno));
			goto leave_cleanup;
		}

		char **strs = NULL;
		if (afc_get_file_info(afc, PKG_PATH, &strs) != AFC_E_SUCCESS) {
			if (afc_make_directory(afc, PKG_PATH) != AFC_E_SUCCESS) {
				fprintf(stderr, "WARNING: Could not create directory '%s' on device!\n", PKG_PATH);
			}
		}
		if (strs) {
			int i = 0;
			while (strs[i]) {
				free(strs[i]);
				i++;
			}
			free(strs);
		}

		plist_t client_opts = instproxy_client_options_new();

		/* open install package */
		int errp = 0;
		struct zip *zf = NULL;

		if ((strlen(cmdarg) > 5) && (strcmp(&cmdarg[strlen(cmdarg)-5], ".ipcc") == 0)) {
			zf = zip_open_path_utf8(cmdarg, &errp);
			if (!zf) {
				fprintf(stderr, "ERROR: zip_open: %s: %d\n", cmdarg, errp);
				goto leave_cleanup;
			}
			char* ipcc = strdup(cmdarg);
			if ((asprintf(&pkgname, "%s/%s", PKG_PATH, basename(ipcc)) > 0) && pkgname) {
				afc_make_directory(afc, pkgname);
			}

			printf("Uploading %s package contents... ", basename(ipcc));

			/* extract the contents of the .ipcc file to PublicStaging/<name>.ipcc directory */
			zip_int64_t numzf = (zip_int64_t)zip_get_num_entries(zf, 0);
			zip_int64_t i = 0;
			for (i = 0; numzf > 0 && i < numzf; i++) {
				const char* zname = zip_get_name(zf, i, 0);
				char* dstpath = NULL;
				if (!zname) continue;
				if (zname[strlen(zname)-1] == '/') {
					// directory
					if ((asprintf(&dstpath, "%s/%s/%s", PKG_PATH, basename(ipcc), zname) > 0) && dstpath) {
						afc_make_directory(afc, dstpath);						}
					free(dstpath);
					dstpath = NULL;
				} else {
					// file
					struct zip_file* zfile = zip_fopen_index(zf, i, 0);
					if (!zfile) continue;

					if ((asprintf(&dstpath, "%s/%s/%s", PKG_PATH, basename(ipcc), zname) <= 0) || !dstpath || (afc_file_open(afc, dstpath, AFC_FOPEN_WRONLY, &af) != AFC_E_SUCCESS)) {
						fprintf(stderr, "ERROR: can't open afc://%s for writing\n", dstpath);
						free(dstpath);
						dstpath = NULL;
						zip_fclose(zfile);
						continue;
					}

					struct zip_stat zs;
					zip_stat_init(&zs);
					if (zip_stat_index(zf, i, 0, &zs) != 0) {
						fprintf(stderr, "ERROR: zip_stat_index %" PRIu64 " failed!\n", i);
						free(dstpath);
						dstpath = NULL;
						zip_fclose(zfile);
						continue;
					}

					free(dstpath);
					dstpath = NULL;

					zip_uint64_t zfsize = 0;
					while (zfsize < zs.size) {
						zip_int64_t amount = zip_fread(zfile, buf, sizeof(buf));
						if (amount == 0) {
							break;
						}

						if (amount > 0) {
							uint32_t written, total = 0;
							while (total < amount) {
								written = 0;
								if (afc_file_write(afc, af, buf, amount, &written) !=
									AFC_E_SUCCESS) {
									fprintf(stderr, "AFC Write error!\n");
									break;
								}
								total += written;
							}
							if (total != amount) {
								fprintf(stderr, "Error: wrote only %d of %" PRIi64 "\n", total, amount);
								afc_file_close(afc, af);
								zip_fclose(zfile);
								free(dstpath);
								goto leave_cleanup;
							}
						}

						zfsize += amount;
					}

					afc_file_close(afc, af);
					af = 0;

					zip_fclose(zfile);
				}
			}
			free(ipcc);
			printf("DONE.\n");

			instproxy_client_options_add(client_opts, "PackageType", "CarrierBundle", NULL);
		} else if (S_ISDIR(fst.st_mode)) {
			/* upload developer app directory */
			instproxy_client_options_add(client_opts, "PackageType", "Developer", NULL);

			if (asprintf(&pkgname, "%s/%s", PKG_PATH, basename(cmdarg)) < 0) {
				fprintf(stderr, "ERROR: Out of memory allocating pkgname!?\n");
				goto leave_cleanup;
			}

			printf("Uploading %s package contents... ", basename(cmdarg));
			afc_upload_dir(afc, cmdarg, pkgname);
			printf("DONE.\n");
			fflush(NULL);

			/* extract the CFBundleIdentifier from the package */

			/* construct full filename to Info.plist */
			char *filename = (char*)malloc(strlen(cmdarg)+11+1);
			strcpy(filename, cmdarg);
			strcat(filename, "/Info.plist");

			path_stat_t st;
			FILE *fp = NULL;

			if (path_stat_utf8(filename, &st) == -1 || (fp = path_fopen_read_utf8(filename)) == NULL) {
				fprintf(stderr, "ERROR: could not locate %s in app!\n", filename);
				free(filename);
				goto leave_cleanup;
			}
			size_t filesize = (size_t)st.st_size;
			char *ibuf = malloc(filesize * sizeof(char));
			size_t amount = fread(ibuf, 1, filesize, fp);
			if (amount != filesize) {
				fprintf(stderr, "ERROR: could not read %u bytes from %s\n", (uint32_t)filesize, filename);
				free(filename);
				goto leave_cleanup;
			}
			fclose(fp);
			free(filename);

			plist_t info = NULL;
			plist_from_memory(ibuf, filesize, &info, NULL);
			free(ibuf);

			if (!info) {
				fprintf(stderr, "ERROR: could not parse Info.plist!\n");
				goto leave_cleanup;
			}

			plist_t bname = plist_dict_get_item(info, "CFBundleIdentifier");
			if (bname) {
				plist_get_string_val(bname, &bundleidentifier);
			}
			plist_free(info);
			info = NULL;
		} else {
			zf = zip_open_path_utf8(cmdarg, &errp);
			if (!zf) {
				fprintf(stderr, "ERROR: zip_open: %s: %d\n", cmdarg, errp);
				goto leave_cleanup;
			}

			char *zbuf = NULL;
			uint32_t len = 0;
			plist_t meta_dict = NULL;

			if (extmeta) {
				size_t flen = 0;
				zbuf = buf_from_file(extmeta, &flen);
				if (zbuf && flen) {
					meta = plist_new_data(zbuf, flen);
					plist_from_memory(zbuf, flen, &meta_dict, NULL);
					free(zbuf);
				}
				if (!meta_dict) {
					plist_free(meta);
					meta = NULL;
					fprintf(stderr, "WARNING: could not load external iTunesMetadata %s!\n", extmeta);
				}
				zbuf = NULL;
			}

			if (!meta && !meta_dict) {
				/* extract iTunesMetadata.plist from package */
				if (zip_get_contents(zf, ITUNES_METADATA_PLIST_FILENAME, 0, &zbuf, &len) == 0) {
					meta = plist_new_data(zbuf, len);
					plist_from_memory(zbuf, len, &meta_dict, NULL);
				}
				if (!meta_dict) {
					plist_free(meta);
					meta = NULL;
					fprintf(stderr, "WARNING: could not locate %s in archive!\n", ITUNES_METADATA_PLIST_FILENAME);
				}
				free(zbuf);
			}

			/* determine .app directory in archive */
			zbuf = NULL;
			len = 0;
			plist_t info = NULL;
			char* filename = NULL;
			char* app_directory_name = NULL;

			if (zip_get_app_directory(zf, &app_directory_name)) {
				fprintf(stderr, "ERROR: Unable to locate .app directory in archive. Make sure it is inside a 'Payload' directory.\n");
				goto leave_cleanup;
			}

			/* construct full filename to Info.plist */
			filename = (char*)malloc(strlen(app_directory_name)+10+1);
			strcpy(filename, app_directory_name);
			free(app_directory_name);
			app_directory_name = NULL;
			strcat(filename, "Info.plist");

			if (zip_get_contents(zf, filename, 0, &zbuf, &len) < 0) {
				fprintf(stderr, "WARNING: could not locate %s in archive!\n", filename);
				free(filename);
				zip_unchange_all(zf);
				zip_close(zf);
				goto leave_cleanup;
			}
			free(filename);
			plist_from_memory(zbuf, len, &info, NULL);
			free(zbuf);

			if (!info) {
				fprintf(stderr, "Could not parse Info.plist!\n");
				zip_unchange_all(zf);
				zip_close(zf);
				goto leave_cleanup;
			}

			char *bundleexecutable = NULL;

			plist_t bname = plist_dict_get_item(info, "CFBundleExecutable");
			if (bname) {
				plist_get_string_val(bname, &bundleexecutable);
			}

			bname = plist_dict_get_item(info, "CFBundleIdentifier");
			if (bname) {
				plist_get_string_val(bname, &bundleidentifier);
			}
			plist_free(info);
			info = NULL;

			if (!bundleexecutable) {
				fprintf(stderr, "Could not determine value for CFBundleExecutable!\n");
				zip_unchange_all(zf);
				zip_close(zf);
				goto leave_cleanup;
			}

			if (extsinf) {
				size_t flen = 0;
				zbuf = buf_from_file(extsinf, &flen);
				if (zbuf && flen) {
					sinf = plist_new_data(zbuf, flen);
					free(zbuf);
				} else {
					fprintf(stderr, "WARNING: could not load external SINF %s!\n", extsinf);
				}
				zbuf = NULL;
			}

			if (!sinf) {
				char *sinfname = NULL;
				if (asprintf(&sinfname, "Payload/%s.app/SC_Info/%s.sinf", bundleexecutable, bundleexecutable) < 0) {
					fprintf(stderr, "Out of memory!?\n");
					goto leave_cleanup;
				}
				free(bundleexecutable);

				/* extract .sinf from package */
				zbuf = NULL;
				len = 0;
				if (zip_get_contents(zf, sinfname, 0, &zbuf, &len) == 0) {
					sinf = plist_new_data(zbuf, len);
				} else {
					fprintf(stderr, "WARNING: could not locate %s in archive!\n", sinfname);
				}
				free(sinfname);
				free(zbuf);
			}

			/* copy archive to device */
			pkgname = NULL;
			if (asprintf(&pkgname, "%s/%s", PKG_PATH, bundleidentifier) < 0) {
				fprintf(stderr, "Out of memory!?\n");
				goto leave_cleanup;
			}

			printf("Copying '%s' to device...\n", cmdarg);
			fflush(stdout);

			if (afc_upload_file(afc, cmdarg, pkgname) < 0) {
				printf("FAILED\n");
				free(pkgname);
				goto leave_cleanup;
			}

			printf("DONE.\n");

			if (bundleidentifier) {
				instproxy_client_options_add(client_opts, "CFBundleIdentifier", bundleidentifier, NULL);
			}
			if (sinf) {
				instproxy_client_options_add(client_opts, "ApplicationSINF", sinf, NULL);
			}
			if (meta) {
				instproxy_client_options_add(client_opts, "iTunesMetadata", meta, NULL);
			}
		}
		if (zf) {
			zip_unchange_all(zf);
			zip_close(zf);
		}

		/* perform installation or upgrade */
		if (cmd == CMD_INSTALL) {
			printf("Installing '%s'\n", bundleidentifier);
			instproxy_install(ipc, pkgname, client_opts, status_cb, NULL);
		} else {
			printf("Upgrading '%s'\n", bundleidentifier);
			instproxy_upgrade(ipc, pkgname, client_opts, status_cb, NULL);
		}
		instproxy_client_options_free(client_opts);
		free(pkgname);
		wait_for_command_complete = 1;
		notification_expected = 1;
	} else if (cmd == CMD_UNINSTALL) {
		printf("Uninstalling '%s'\n", cmdarg);
		instproxy_uninstall(ipc, cmdarg, NULL, status_cb, NULL);
		wait_for_command_complete = 1;
		notification_expected = 0;
	} else if (cmd == CMD_LIST_ARCHIVES) {
		plist_t dict = NULL;

		err = instproxy_lookup_archives(ipc, NULL, &dict);
		if (err != INSTPROXY_E_SUCCESS) {
			fprintf(stderr, "ERROR: lookup_archives returned %d\n", err);
			goto leave_cleanup;
		}

		if (!dict) {
			fprintf(stderr, "ERROR: lookup_archives did not return a plist!?\n");
			goto leave_cleanup;
		}

		if (output_format) {
			char *buf = NULL;
			uint32_t len = 0;
			if (output_format == FORMAT_XML) {
				plist_err_t perr = plist_to_xml(dict, &buf, &len);
				if (perr != PLIST_ERR_SUCCESS) {
					fprintf(stderr, "ERROR: Failed to convert data to XML format (%d).\n", perr);
				}
			} else if (output_format == FORMAT_JSON) {
				plist_err_t perr = plist_to_json(dict, &buf, &len, 1);
				if (perr != PLIST_ERR_SUCCESS) {
					fprintf(stderr, "ERROR: Failed to convert data to JSON format (%d).\n", perr);
				}
			}
			if (buf) {
				puts(buf);
				free(buf);
			}
			plist_free(dict);
			goto leave_cleanup;
		}
		plist_dict_iter iter = NULL;
		plist_t node = NULL;
		char *key = NULL;

		printf("Total: %d archived apps\n", plist_dict_get_size(dict));
		plist_dict_new_iter(dict, &iter);
		if (!iter) {
			plist_free(dict);
			fprintf(stderr, "ERROR: Could not create plist_dict_iter!\n");
			goto leave_cleanup;
		}
		do {
			key = NULL;
			node = NULL;
			plist_dict_next_item(dict, iter, &key, &node);
			if (key && (plist_get_node_type(node) == PLIST_DICT)) {
				char *s_dispName = NULL;
				char *s_version = NULL;
				plist_t dispName =
					plist_dict_get_item(node, "CFBundleDisplayName");
				plist_t version =
					plist_dict_get_item(node, "CFBundleShortVersionString");
				if (dispName) {
					plist_get_string_val(dispName, &s_dispName);
				}
				if (version) {
					plist_get_string_val(version, &s_version);
				}
				if (!s_dispName) {
					s_dispName = strdup(key);
				}
				if (s_version) {
					printf("%s - %s %s\n", key, s_dispName, s_version);
					free(s_version);
				} else {
					printf("%s - %s\n", key, s_dispName);
				}
				free(s_dispName);
				free(key);
			}
		}
		while (node);
		plist_free(dict);
	} else if (cmd == CMD_ARCHIVE) {
		plist_t client_opts = NULL;

		if (skip_uninstall || app_only || docs_only) {
			client_opts = instproxy_client_options_new();
			if (skip_uninstall) {
				instproxy_client_options_add(client_opts, "SkipUninstall", 1, NULL);
			}
			if (app_only) {
				instproxy_client_options_add(client_opts, "ArchiveType", "ApplicationOnly", NULL);
			} else if (docs_only) {
				instproxy_client_options_add(client_opts, "ArchiveType", "DocumentsOnly", NULL);
			}
		}

		if (copy_path) {
			path_stat_t fst;
			if (path_stat_utf8(copy_path, &fst) != 0) {
				fprintf(stderr, "ERROR: stat: %s: %s\n", copy_path, strerror(errno));
				goto leave_cleanup;
			}

			if (!S_ISDIR(fst.st_mode)) {
				fprintf(stderr, "ERROR: '%s' is not a directory as expected.\n", copy_path);
				goto leave_cleanup;
			}

			if (service) {
				lockdownd_service_descriptor_free(service);
			}
			service = NULL;

			if ((lockdownd_start_service(client, "com.apple.afc", &service) != LOCKDOWN_E_SUCCESS) || !service) {
				fprintf(stderr, "Could not start com.apple.afc!\n");
				goto leave_cleanup;
			}

			lockdownd_client_free(client);
			client = NULL;

			if (afc_client_new(device, service, &afc) != AFC_E_SUCCESS) {
				fprintf(stderr, "Could not connect to AFC!\n");
				goto leave_cleanup;
			}
		}

		instproxy_archive(ipc, cmdarg, client_opts, status_cb, NULL);

		instproxy_client_options_free(client_opts);
		wait_for_command_complete = 1;
		if (skip_uninstall) {
			notification_expected = 0;
		} else {
			notification_expected = 1;
		}

		idevice_wait_for_command_to_complete();

		if (copy_path) {
			if (err_occurred) {
				afc_client_free(afc);
				afc = NULL;
				goto leave_cleanup;
			}
			FILE *f = NULL;
			uint64_t af = 0;
			/* local filename */
			char *localfile = NULL;
			if (asprintf(&localfile, "%s/%s.ipa", copy_path, cmdarg) < 0) {
				fprintf(stderr, "Out of memory!?\n");
				goto leave_cleanup;
			}

			f = path_fopen_utf8(localfile, "wb");
			if (!f) {
				fprintf(stderr, "ERROR: fopen: %s: %s\n", localfile, strerror(errno));
				free(localfile);
				goto leave_cleanup;
			}

			/* remote filename */
			char *remotefile = NULL;
			if (asprintf(&remotefile, "%s/%s.zip", APPARCH_PATH, cmdarg) < 0) {
				fprintf(stderr, "Out of memory!?\n");
				goto leave_cleanup;
			}

			uint32_t fsize = 0;
			char **fileinfo = NULL;
			if ((afc_get_file_info(afc, remotefile, &fileinfo) != AFC_E_SUCCESS) || !fileinfo) {
				fprintf(stderr, "ERROR getting AFC file info for '%s' on device!\n", remotefile);
				fclose(f);
				free(remotefile);
				free(localfile);
				goto leave_cleanup;
			}

			int i;
			for (i = 0; fileinfo[i]; i+=2) {
				if (!strcmp(fileinfo[i], "st_size")) {
					fsize = atoi(fileinfo[i+1]);
					break;
				}
			}
			i = 0;
			while (fileinfo[i]) {
				free(fileinfo[i]);
				i++;
			}
			free(fileinfo);

			if (fsize == 0) {
				fprintf(stderr, "Hm... remote file length could not be determined. Cannot copy.\n");
				fclose(f);
				free(remotefile);
				free(localfile);
				goto leave_cleanup;
			}

			if ((afc_file_open(afc, remotefile, AFC_FOPEN_RDONLY, &af) != AFC_E_SUCCESS) || !af) {
				fclose(f);
				fprintf(stderr, "ERROR: could not open '%s' on device for reading!\n", remotefile);
				free(remotefile);
				free(localfile);
				goto leave_cleanup;
			}

			/* copy file over */
			printf("Copying '%s' --> '%s'...\n", remotefile, localfile);
			fflush(stdout);
			free(remotefile);
			free(localfile);

			uint32_t amount = 0;
			uint32_t total = 0;
			char buf[8192];

			do {
				if (afc_file_read(afc, af, buf, sizeof(buf), &amount) != AFC_E_SUCCESS) {
					fprintf(stderr, "AFC Read error!\n");
					break;
				}

				if (amount > 0) {
					size_t written = fwrite(buf, 1, amount, f);
					if (written != amount) {
						fprintf(stderr, "Error when writing %d bytes to local file!\n", amount);
						break;
					}
					total += written;
				}
			} while (amount > 0);

			afc_file_close(afc, af);
			fclose(f);

			printf("DONE.\n");

			if (total != fsize) {
				fprintf(stderr, "WARNING: remote and local file sizes don't match (%d != %d)\n", fsize, total);
				if (remove_after_copy) {
					fprintf(stderr, "NOTE: archive file will NOT be removed from device\n");
					remove_after_copy = 0;
				}
			}

			if (remove_after_copy) {
				/* remove archive if requested */
				printf("Removing '%s'\n", cmdarg);
				cmd = CMD_REMOVE_ARCHIVE;
				if (LOCKDOWN_E_SUCCESS != lockdownd_client_new_with_handshake(device, &client, "ideviceinstaller")) {
					fprintf(stderr, "Could not connect to lockdownd. Exiting.\n");
					goto leave_cleanup;
				}
				goto run_again;
			}
		}
		goto leave_cleanup;
	} else if (cmd == CMD_RESTORE) {
		instproxy_restore(ipc, cmdarg, NULL, status_cb, NULL);
		wait_for_command_complete = 1;
		notification_expected = 1;
	} else if (cmd == CMD_REMOVE_ARCHIVE) {
		instproxy_remove_archive(ipc, cmdarg, NULL, status_cb, NULL);
		wait_for_command_complete = 1;
	} else {
		printf("ERROR: no command selected?! This should not be reached!\n");
		res = 2;
		goto leave_cleanup;
	}

	/* not needed anymore */
	lockdownd_client_free(client);
	client = NULL;

	idevice_wait_for_command_to_complete();
	res = 0;

leave_cleanup:
	np_client_free(np);
	instproxy_client_free(ipc);
	afc_client_free(afc);
	lockdownd_client_free(client);
	idevice_free(device);

	free(udid);
	free(copy_path);
	free(extsinf);
	free(extmeta);
	free(bundleidentifier);
	plist_free(bundle_ids);
	plist_free(return_attrs);

	if (err_occurred && !res) {
		res = 128;
	}

	return res;
}
