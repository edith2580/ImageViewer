using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.Services.ImageSources;
using ImageViewer.ViewModels;

namespace ImageViewer.IntegrationTests;

public class FileTrashTests
{
    [Fact]
    public void Moves_file_into_freedesktop_trash_with_info()
    {
        using var temp = new TempFolder();
        var trash = Path.Combine(temp.Path, "Trash");
        var photos = Path.Combine(temp.Path, "my photos");
        Directory.CreateDirectory(photos);
        var file = Path.Combine(photos, "a b.png");
        File.WriteAllBytes(file, TestImages.Png());

        var deletedAt = new DateTime(2026, 11, 25, 9, 8, 7);
        var first = FileTrash.MoveToFreedesktopTrash(file, trash, deletedAt);

        Assert.False(File.Exists(file));
        Assert.Equal(Path.Combine(trash, "files", "a b.png"), first);
        Assert.True(File.Exists(first));
        var info = File.ReadAllText(Path.Combine(trash, "info", "a b.png.trashinfo"));
        Assert.StartsWith("[Trash Info]\n", info);
        Assert.Contains("DeletionDate=2026-11-25T09:08:07\n", info);
        Assert.Contains("a%20b.png", info);

        // 같은 이름을 다시 지우면 겹치지 않는 이름으로 옮긴다.
        File.WriteAllBytes(file, TestImages.Png());
        var second = FileTrash.MoveToFreedesktopTrash(file, trash, deletedAt);
        Assert.Equal(Path.Combine(trash, "files", "a b (2).png"), second);
        Assert.True(File.Exists(Path.Combine(trash, "info", "a b (2).png.trashinfo")));
    }

    [Fact]
    public void Trash_info_escapes_path_but_keeps_slashes()
    {
        var text = FileTrash.BuildTrashInfo("/home/user/사진 폴더/a#1.jpg", new DateTime(2026, 1, 2, 3, 4, 5));
        Assert.Equal(
            "[Trash Info]\nPath=/home/user/%EC%82%AC%EC%A7%84%20%ED%8F%B4%EB%8D%94/a%231.jpg\nDeletionDate=2026-01-02T03:04:05\n",
            text);
    }

    [Fact]
    public void Missing_file_is_reported()
    {
        using var temp = new TempFolder();
        Assert.Throws<FileNotFoundException>(() => FileTrash.MoveToTrash(temp.File("none.png")));
    }

    [Fact]
    public void Only_plain_files_from_a_folder_can_be_deleted()
    {
        using var temp = new TempFolder();
        using var folder = new FolderImageSource(temp.Path);
        var file = new ImageInfo("a.png", ImageSourceType.File, temp.File("a.png"), null, 10, null);
        var zipEntry = new ImageInfo("a.png", ImageSourceType.Zip, temp.File("x.zip"), "dir/a.png", 10, null);

        Assert.True(MainViewModel.CanDeleteFile(folder, file));
        Assert.False(MainViewModel.CanDeleteFile(folder, zipEntry));
        Assert.False(MainViewModel.CanDeleteFile(null, file));
        Assert.False(MainViewModel.CanDeleteFile(folder, null));
    }
}
