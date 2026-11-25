using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace ImageViewer.Services;

/// <summary>
/// 파일을 영구 삭제하지 않고 휴지통으로 옮긴다.
/// Windows: 셸(SHFileOperation, 실행 취소 가능), macOS: ~/.Trash,
/// Linux: gio trash, 없으면 freedesktop 휴지통 규격(~/.local/share/Trash)대로 직접 옮긴다.
/// </summary>
public static class FileTrash
{
    /// <summary>파일을 휴지통으로 옮긴다. 실패하면 사용자 안내용 메시지와 함께 <see cref="IOException"/>.</summary>
    public static void MoveToTrash(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("파일이 없습니다.", path);
        }

        if (OperatingSystem.IsWindows())
        {
            MoveToRecycleBinWindows(path);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash");
            Directory.CreateDirectory(trash);
            File.Move(path, UniquePath(trash, Path.GetFileName(path)));
        }
        else if (!TryGioTrash(path))
        {
            MoveToFreedesktopTrash(path, FreedesktopTrashDirectory(), DateTime.Now);
        }

        if (File.Exists(path))
        {
            throw new IOException("파일을 휴지통으로 옮기지 못했습니다.");
        }
    }

    #region Windows

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;
    private const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    private static void MoveToRecycleBinWindows(string path)
    {
        var operation = new SHFILEOPSTRUCTW
        {
            wFunc = FO_DELETE,
            pFrom = Path.GetFullPath(path) + "\0\0", // 두 번의 NUL 로 끝나는 목록
            // 휴지통을 쓸 수 없는 위치(네트워크 드라이브 등)면 영구 삭제 전에 Windows 가 한 번 더 묻는다.
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };

        var result = SHFileOperation(ref operation);
        if (operation.fAnyOperationsAborted)
        {
            throw new OperationCanceledException("삭제를 취소했습니다.");
        }

        if (result != 0)
        {
            throw new IOException($"파일을 휴지통으로 옮기지 못했습니다. (오류 0x{result:X})");
        }
    }

    #endregion

    #region Linux (freedesktop.org Trash 규격)

    private static bool TryGioTrash(string path)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gio")
            {
                ArgumentList = { "trash", "--", path },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);
            return process.HasExited && process.ExitCode == 0 && !File.Exists(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // gio 가 없다
        }
    }

    private static string FreedesktopTrashDirectory()
    {
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        return Path.Combine(dataHome, "Trash");
    }

    /// <summary>휴지통의 files/ 로 옮기고 info/ 에 원래 경로 · 삭제 시각(.trashinfo)을 남긴다 (복원 가능).</summary>
    internal static string MoveToFreedesktopTrash(string path, string trashDirectory, DateTime deletedAt)
    {
        var files = Path.Combine(trashDirectory, "files");
        var info = Path.Combine(trashDirectory, "info");
        Directory.CreateDirectory(files);
        Directory.CreateDirectory(info);

        var fullPath = Path.GetFullPath(path);
        var target = UniquePath(files, Path.GetFileName(fullPath),
            name => !File.Exists(Path.Combine(info, name + ".trashinfo")));
        var name = Path.GetFileName(target);
        var infoPath = Path.Combine(info, name + ".trashinfo");

        File.WriteAllText(infoPath, BuildTrashInfo(fullPath, deletedAt), new UTF8Encoding(false));
        try
        {
            File.Move(fullPath, target);
        }
        catch
        {
            File.Delete(infoPath);
            throw;
        }

        return target;
    }

    internal static string BuildTrashInfo(string fullPath, DateTime deletedAt) =>
        "[Trash Info]\n" +
        $"Path={EscapePath(fullPath)}\n" +
        $"DeletionDate={deletedAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";

    /// <summary>경로를 URL 인코딩한다 ('/' 는 그대로).</summary>
    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    #endregion

    /// <summary>폴더 안에서 겹치지 않는 이름을 고른다: name.jpg, name (2).jpg, name (3).jpg ...</summary>
    internal static string UniquePath(string directory, string fileName, Func<string, bool>? isFree = null)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? fileName : $"{stem} ({i}){extension}";
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate) && !Directory.Exists(candidate) && (isFree?.Invoke(name) ?? true))
            {
                return candidate;
            }
        }
    }
}
