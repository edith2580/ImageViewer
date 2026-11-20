using ImageViewer.Models;
using ImageViewer.Services;
using ImageViewer.ViewModels;

namespace ImageViewer.Tests;

public class ComicModeTests
{
    [Fact]
    public void Spread_navigation_moves_by_two_pages_and_wraps()
    {
        // 5장: (0,1), (2,3), (4,null)
        Assert.Equal(3, ComicSpreadHelper.GetSpreadCount(5));
        Assert.Equal(0, ComicSpreadHelper.GetSpreadStart(1, 5));
        Assert.Equal(2, ComicSpreadHelper.GetSpreadStart(3, 5));
        Assert.Equal(4, ComicSpreadHelper.GetLastSpreadStart(5));

        Assert.Equal(2, ComicSpreadHelper.GetNextSpreadStart(0, 5, wrapAround: true));
        Assert.Equal(4, ComicSpreadHelper.GetNextSpreadStart(2, 5, wrapAround: true));
        Assert.Equal(0, ComicSpreadHelper.GetNextSpreadStart(4, 5, wrapAround: true));
        Assert.Equal(-1, ComicSpreadHelper.GetNextSpreadStart(4, 5, wrapAround: false));

        Assert.Equal(4, ComicSpreadHelper.GetPreviousSpreadStart(0, 5, wrapAround: true));
        Assert.Equal(-1, ComicSpreadHelper.GetPreviousSpreadStart(0, 5, wrapAround: false));
        Assert.Equal(2, ComicSpreadHelper.GetPreviousSpreadStart(4, 5, wrapAround: true));
    }

    [Fact]
    public void Spread_ordering_supports_left_to_right_and_right_to_left()
    {
        Assert.Equal((0, 1), ComicSpreadHelper.OrderIndices(0, 1, ComicReadingDirection.LeftToRight));
        Assert.Equal((1, 0), ComicSpreadHelper.OrderIndices(0, 1, ComicReadingDirection.RightToLeft));

        // 홀수 마지막 페이지
        Assert.Equal((4, null), ComicSpreadHelper.OrderIndices(4, null, ComicReadingDirection.LeftToRight));
        Assert.Equal((null, 4), ComicSpreadHelper.OrderIndices(4, null, ComicReadingDirection.RightToLeft));
    }

    [Fact]
    public void Comic_thumbnail_pair_switches_left_and_right_with_direction()
    {
        var i1 = new ImageInfo("1", ImageSourceType.File, "01.jpg", null, 100, null);
        var i2 = new ImageInfo("2", ImageSourceType.File, "02.jpg", null, 200, null);
        var t1 = new ThumbnailItemViewModel(0, i1, _ => { });
        var t2 = new ThumbnailItemViewModel(1, i2, _ => { });

        var pair = new ComicThumbnailItemViewModel(0, t1, t2, ComicReadingDirection.LeftToRight);
        Assert.Same(t1, pair.LeftItem);
        Assert.Same(t2, pair.RightItem);

        pair.SetDirection(ComicReadingDirection.RightToLeft);
        Assert.Same(t2, pair.LeftItem);
        Assert.Same(t1, pair.RightItem);
        pair.Detach();
    }

    [Fact]
    public void Image_list_view_model_groups_spreads_in_comic_mode()
    {
        var images = new[]
        {
            new ImageInfo("01.jpg", ImageSourceType.File, "01.jpg", null, 100, null),
            new ImageInfo("02.jpg", ImageSourceType.File, "02.jpg", null, 200, null),
            new ImageInfo("03.jpg", ImageSourceType.File, "03.jpg", null, 300, null),
        };

        var ltr = new ImageListViewModel(images, currentIndex: 1, source: null, isComicMode: true, comicDirection: ComicReadingDirection.LeftToRight);
        Assert.True(ltr.IsComicMode);
        Assert.Equal(2, ltr.Items.Count);
        Assert.Equal(0, ltr.CurrentIndex);
        Assert.Contains("[1p | 2p]", ltr.Items[0].Name);
        Assert.Contains("01.jpg  |  02.jpg", ltr.Items[0].Name);
        Assert.Contains("[3p]", ltr.Items[1].Name);

        var rtl = new ImageListViewModel(images, currentIndex: 2, source: null, isComicMode: true, comicDirection: ComicReadingDirection.RightToLeft);
        Assert.Equal(2, rtl.CurrentIndex);
        Assert.Contains("[2p | 1p]", rtl.Items[0].Name);
        Assert.Contains("02.jpg  |  01.jpg", rtl.Items[0].Name);
        Assert.True(rtl.Accept());
        Assert.Equal(2, rtl.ResultIndex);
    }

    [Fact]
    public void Wide_and_square_images_are_detected_and_shown_as_single_spread()
    {
        // 세로(1페이지): false, 정사각형/가로(2페이지): true
        Assert.False(ComicSpreadHelper.IsWideImage(800, 1200));
        Assert.True(ComicSpreadHelper.IsWideImage(1000, 1000));
        Assert.True(ComicSpreadHelper.IsWideImage(1600, 1200));
        // EXIF 90도 회전 시 가로·세로 반전
        Assert.False(ComicSpreadHelper.IsWideImage(1600, 1200, swapped: true));
        Assert.True(ComicSpreadHelper.IsWideImage(800, 1200, swapped: true));

        // 0:가로(단독), 1:세로, 2:세로 (1-2 짝), 3:세로(다음이 가로라 단독), 4:정사각형(단독), 5:세로, 6:세로 (5-6 짝)
        bool[] isWide = [true, false, false, false, true, false, false];
        var spreads = ComicSpreadHelper.BuildSpreads(7, isWide);

        Assert.Equal(5, spreads.Count);
        Assert.Equal(new ComicSpread(0, 0, null, true), spreads[0]);
        Assert.Equal(new ComicSpread(1, 1, 2, false), spreads[1]);
        Assert.Equal(new ComicSpread(2, 3, null, false), spreads[2]);
        Assert.Equal(new ComicSpread(3, 4, null, true), spreads[3]);
        Assert.Equal(new ComicSpread(4, 5, 6, false), spreads[4]);

        // 탐색 검증
        Assert.Equal(1, ComicSpreadHelper.GetNextSpreadStart(spreads, 0, wrapAround: false));
        Assert.Equal(3, ComicSpreadHelper.GetNextSpreadStart(spreads, 1, wrapAround: false));
        Assert.Equal(4, ComicSpreadHelper.GetNextSpreadStart(spreads, 3, wrapAround: false));
        Assert.Equal(5, ComicSpreadHelper.GetNextSpreadStart(spreads, 4, wrapAround: false));
        Assert.Equal(0, ComicSpreadHelper.GetNextSpreadStart(spreads, 5, wrapAround: true));
        Assert.Equal(3, ComicSpreadHelper.GetPreviousSpreadStart(spreads, 4, wrapAround: false));
    }

    [Fact]
    public void Image_list_marks_wide_spread_as_single_page()
    {
        var images = new[]
        {
            new ImageInfo("cover.jpg", ImageSourceType.File, "cover.jpg", null, 100, null),
            new ImageInfo("p1.jpg", ImageSourceType.File, "p1.jpg", null, 100, null),
            new ImageInfo("p2.jpg", ImageSourceType.File, "p2.jpg", null, 100, null),
        };
        var spreads = ComicSpreadHelper.BuildSpreads(3, [true, false, false]);

        var vm = new ImageListViewModel(images, currentIndex: 0, source: null, isComicMode: true, comicSpreads: spreads);
        Assert.Equal(2, vm.Items.Count);
        Assert.Contains("[1p · 양면]", vm.Items[0].Name);
        Assert.Contains("[2p | 3p]", vm.Items[1].Name);
    }

    [Fact]
    public void Manual_blank_page_splits_current_pair_and_shifts_following_spreads()
    {
        // 기본 6장: (0,1), (2,3), (4,5)
        var baseSpreads = ComicSpreadHelper.BuildSpreads(6);
        Assert.Equal(3, baseSpreads.Count);
        Assert.Equal(new ComicSpread(0, 0, 1, false), baseSpreads[0]);
        Assert.Equal(new ComicSpread(1, 2, 3, false), baseSpreads[1]);
        Assert.Equal(new ComicSpread(2, 4, 5, false), baseSpreads[2]);

        // 0번(1p)과 1번(2p) 사이에 빈 페이지 수동 삽입 → [0, 빈], [1, 2], [3, 4], [5]
        var manualBlanks = new HashSet<int> { 0 };
        var shifted = ComicSpreadHelper.BuildSpreads(6, manualBlankAfter: manualBlanks);
        Assert.Equal(4, shifted.Count);
        Assert.Equal(new ComicSpread(0, 0, null, false, TrailingBlank: true), shifted[0]);
        Assert.Equal(new ComicSpread(1, 1, 2, false), shifted[1]);
        Assert.Equal(new ComicSpread(2, 3, 4, false), shifted[2]);
        Assert.Equal(new ComicSpread(3, 5, null, false), shifted[3]);

        // 3번(4p)과 4번(5p) 사이에 빈 페이지 하나 더 추가 → [0, 빈], [1, 2], [3, 빈], [4, 5]
        manualBlanks.Add(3);
        var shiftedTwice = ComicSpreadHelper.BuildSpreads(6, manualBlankAfter: manualBlanks);
        Assert.Equal(4, shiftedTwice.Count);
        Assert.Equal(new ComicSpread(0, 0, null, false, TrailingBlank: true), shiftedTwice[0]);
        Assert.Equal(new ComicSpread(1, 1, 2, false), shiftedTwice[1]);
        Assert.Equal(new ComicSpread(2, 3, null, false, TrailingBlank: true), shiftedTwice[2]);
        Assert.Equal(new ComicSpread(3, 4, 5, false), shiftedTwice[3]);

        // 빈 페이지 제거 시 원래대로 복원
        manualBlanks.Clear();
        var restored = ComicSpreadHelper.BuildSpreads(6, manualBlankAfter: manualBlanks);
        Assert.Equal(baseSpreads, restored);
    }

    [Fact]
    public void Manual_blank_page_renders_blank_slot_in_thumbnails_and_image_list()
    {
        var images = new[] { "01.jpg", "02.jpg", "03.jpg" }
            .Select(n => new ImageInfo(n, ImageSourceType.File, n, null, 100, null))
            .ToArray();
        var spreads = ComicSpreadHelper.BuildSpreads(images.Length, manualBlankAfter: new HashSet<int> { 0 });

        // 썸네일 방향별 빈 슬롯 확인
        var t0 = new ThumbnailItemViewModel(0, images[0], _ => { });
        var thumb = new ComicThumbnailItemViewModel(0, t0, null, ComicReadingDirection.LeftToRight, trailingBlank: true);
        Assert.Same(t0, thumb.LeftItem);
        Assert.True(thumb.IsRightBlank);
        Assert.False(thumb.IsLeftBlank);

        thumb.SetDirection(ComicReadingDirection.RightToLeft);
        Assert.Same(t0, thumb.RightItem);
        Assert.True(thumb.IsLeftBlank);
        Assert.False(thumb.IsRightBlank);
        thumb.Detach();

        // Enter 이미지 목록 표시 확인
        var ltrList = new ImageListViewModel(images, currentIndex: 0, source: null, isComicMode: true, comicDirection: ComicReadingDirection.LeftToRight, comicSpreads: spreads);
        Assert.Equal(2, ltrList.Items.Count);
        Assert.Contains("[1p | 빈]", ltrList.Items[0].Name);
        Assert.Contains("[2p | 3p]", ltrList.Items[1].Name);

        var rtlList = new ImageListViewModel(images, currentIndex: 0, source: null, isComicMode: true, comicDirection: ComicReadingDirection.RightToLeft, comicSpreads: spreads);
        Assert.Contains("[빈 | 1p]", rtlList.Items[0].Name);
        Assert.Contains("[3p | 2p]", rtlList.Items[1].Name);
    }
}
