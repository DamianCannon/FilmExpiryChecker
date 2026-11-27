using FilmExpiryChecker.Launching;
using System.Runtime.InteropServices;

namespace FilmExpiryChecker.Launcher;

/// <summary>The few Windows calls the launcher needs.</summary>
internal static partial class NativeMethods
{
    /// <summary>
    /// Film Expiry Checker's taskbar identity, on the launcher's process and on its shortcuts, so its window
    /// sits under the pinned icon. Changing it breaks the user's pin: settle it before they pin.
    /// </summary>
    internal const string AppId = "DamianCannon.FilmExpiryChecker";

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    /// <summary>Lets the launcher already running take the foreground when a second click asks it to.</summary>
    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);

    internal const int AnyProcess = -1;

    // Ctrl+C sent to the app, so it stops as it would in a terminal (its background work finished).
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCtrlHandler(IntPtr handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);

    internal const uint CtrlC = 0;
}
