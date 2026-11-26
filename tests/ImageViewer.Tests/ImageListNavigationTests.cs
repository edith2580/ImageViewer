using Avalonia.Input;
using ImageViewer.Models;
using ImageViewer.ViewModels;
using ImageViewer.Views;

namespace ImageViewer.Tests;

/// <summary>이미지 목록(Enter) 대화 상자: 키보드로 옮긴 위치가 실제 선택이 되어 Enter 로 그 이미지가 열린다.</summary>
public class ImageListNavigationTests
{
    [Theory]
    [InlineData(5, ListNavigation.Up, 10, 4)]
    [InlineData(5, ListNavigation.Down, 10, 6)]
    [InlineData(5, ListNavigation.PageDown, 10, 15)]
    [InlineData(5, ListNavigation.PageUp, 10, 0)]   // 처음에서 멈춘다
    [InlineData(95, ListNavigation.PageDown, 10, 99)] // 끝에서 멈춘다
    [InlineData(0, ListNavigation.Up, 10, 0)]
    [InlineData(99, ListNavigation.Down, 10, 99)]
    [InlineData(42, ListNavigation.Home, 10, 0)]
    [InlineData(42, ListNavigation.End, 10, 99)]
    [InlineData(-1, ListNavigation.Down, 10, 0)]    // 선택이 없으면 처음부터
    [InlineData(-1, ListNavigation.End, 10, 99)]
    [InlineData(5, ListNavigation.PageDown, 0, 6)]  // 줄 수를 모르면 최소 1줄
    public void Navigation_moves_within_the_list(int current, ListNavigation navigation, int pageSize, int expected) =>
        Assert.Equal(expected, ImageListViewModel.NavigateIndex(current, 100, navigation, pageSize));

    [Theory]
    [InlineData(Key.Up, ListNavigation.Up)]
    [InlineData(Key.Down, ListNavigation.Down)]
    [InlineData(Key.PageUp, ListNavigation.PageUp)]
    [InlineData(Key.PageDown, ListNavigation.PageDown)]
    [InlineData(Key.Home, ListNavigation.Home)]
    [InlineData(Key.End, ListNavigation.End)]
    public void Navigation_keys_are_mapped(Key key, ListNavigation expected) =>
        Assert.Equal(expected, ImageListWindow.ToNavigation(key));

    [Fact]
    public void Other_keys_are_not_navigation() => Assert.Null(ImageListWindow.ToNavigation(Key.A));

    private static IReadOnlyList<ImageInfo> Images(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new ImageInfo($"img{i:00}.png", ImageSourceType.File, $"/x/img{i:00}.png", null, 1000 + i, new DateTime(2026, 1, 1).AddMinutes(i)))
            .ToList();

    [Fact]
    public void Page_down_selects_the_item_that_enter_opens()
    {
        var vm = new ImageListViewModel(Images(60), currentIndex: 0, source: null);

        vm.MoveSelection(ListNavigation.PageDown, 15);
        vm.MoveSelection(ListNavigation.PageDown, 15);
        vm.MoveSelection(ListNavigation.Up, 15);

        Assert.Equal(29, vm.SelectedItem!.Index);
        Assert.Equal(29, vm.SelectedThumbnailIndex); // 상단 썸네일 줄도 같은 위치
        Assert.True(vm.Accept());
        Assert.Equal(29, vm.ResultIndex);
    }

    [Fact]
    public void Navigation_follows_the_visible_sort_order()
    {
        var vm = new ImageListViewModel(Images(10), currentIndex: 3, source: null);
        vm.SortByCommand.Execute(ImageListSortColumn.Size); // 크기 오름차순 (= 원래 순서)
        vm.SortByCommand.Execute(ImageListSortColumn.Size); // 크기 내림차순: 9, 8, 7, ...

        vm.MoveSelection(ListNavigation.Down, 5);
        Assert.Equal(2, vm.SelectedItem!.Index); // 화면에서 3 바로 아래는 2

        vm.MoveSelection(ListNavigation.Home, 5);
        Assert.Equal(9, vm.SelectedItem!.Index); // 화면 맨 위는 가장 큰 9
    }

    [Theory]
    [InlineData(1500, 600, 6000, 1200)] // 가운데: 항목 가운데(1500) - 화면 절반(300)
    [InlineData(100, 600, 6000, 0)]     // 처음 근처: 왼쪽 끝까지만
    [InlineData(5950, 600, 6000, 5400)] // 끝 근처: 오른쪽 끝까지만 (6000 - 600)
    [InlineData(300, 600, 500, 0)]      // 줄이 화면보다 짧으면 스크롤하지 않는다
    public void Thumbnail_is_centered_when_possible(double itemCenter, double viewport, double extent, double expected) =>
        Assert.Equal(expected, ImageViewer.Helpers.ThumbnailStripHelper.CenteredOffset(itemCenter, viewport, extent));

    [Fact]
    public void Empty_list_has_nothing_to_select()
    {
        var vm = new ImageListViewModel([], currentIndex: -1, source: null);
        Assert.Null(vm.MoveSelection(ListNavigation.Down, 10));
        Assert.False(vm.Accept());
    }
}
