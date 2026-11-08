AM_CFLAGS =			\
	$(GLOBAL_CFLAGS)	\
	$(libimobiledevice_CFLAGS)	\
	$(libglib2_CFLAGS)	\
	$(libplist_CFLAGS)	\
	$(libzip_CFLAGS)

AM_LDFLAGS =			\
	$(libimobiledevice_LIBS)	\
	$(libglib2_LIBS)	\
	$(libplist_LIBS)	\
	$(libzip_LIBS)

bin_PROGRAMS = ideviceinstaller

ideviceinstaller_SOURCES = ideviceinstaller.c
ideviceinstaller_CFLAGS = $(AM_CFLAGS)
ideviceinstaller_LDFLAGS = $(AM_LDFLAGS)

EXTRA_DIST = app_res.rc ideviceinstaller.manifest

# Windows only: embed the application manifest (longPathAware) into the
# executable.  HAVE_WINDRES is set by configure when windres is available, so
# POSIX builds are unaffected.
if HAVE_WINDRES
app_res.o: $(srcdir)/app_res.rc $(srcdir)/ideviceinstaller.manifest
	$(WINDRES) -I $(srcdir) -i $< -o $@

ideviceinstaller_LDADD = app_res.o
CLEANFILES = app_res.o
endif

