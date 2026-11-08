using System.Runtime.InteropServices;

namespace AfcHelper;

/// <summary>
/// libimobiledevice 的 P/Invoke 声明。只声明 helper 用到的符号——
/// 全部在随程序分发的 libimobiledevice-1.0.dll 中导出（总览 §6.4）。
/// 声明与主程序旧代码 Afc.cs、afcspike/Program.cs 一致，不要改调用约定。
/// </summary>
internal static class Native
{
    private const string Lib = "libimobiledevice-1.0.dll";

    /// <summary>idevice_new_with_options 的 options：1 = 走 usbmuxd（USB）。</summary>
    public const int LookupUsbmux = 1;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_new_with_options(out IntPtr device, IntPtr udid, int options);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int idevice_free(IntPtr device);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_client_new_with_handshake(IntPtr device, out IntPtr client, IntPtr label);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_start_service(IntPtr client, IntPtr identifier, out IntPtr service);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_client_free(IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int lockdownd_service_descriptor_free(IntPtr service);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_client_new(IntPtr device, IntPtr service, out IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_client_free(IntPtr client);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_read_directory(IntPtr client, IntPtr path, out IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_get_file_info(IntPtr client, IntPtr path, out IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_get_device_info(IntPtr client, out IntPtr info);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_dictionary_free(IntPtr dict);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int afc_remove_path(IntPtr client, IntPtr path);
}
