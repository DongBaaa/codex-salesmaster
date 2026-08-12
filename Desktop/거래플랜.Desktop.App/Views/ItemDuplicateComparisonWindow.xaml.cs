using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class ItemDuplicateComparisonWindow : Window, INotifyPropertyChanged
{
    private DataIntegrityItemDuplicateCandidate? _selectedCandidate;

    public ItemDuplicateComparisonWindow(DataIntegrityItemDuplicateReviewPreparation review)
    {
        Review = review ?? throw new ArgumentNullException(nameof(review));
        Comparison = review.Comparison;
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public DataIntegrityItemDuplicateReviewPreparation Review { get; }
    public DataIntegrityItemDuplicateComparison Comparison { get; }

    public DataIntegrityItemDuplicateCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        private set
        {
            if (ReferenceEquals(_selectedCandidate, value))
                return;

            _selectedCandidate = value;
            OnPropertyChanged();
        }
    }

    public Guid? SelectedCanonicalItemId { get; private set; }
    public Guid? RequestedItemId { get; private set; }

    private void CandidateGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SelectedCandidate = CandidateGrid.SelectedItem as DataIntegrityItemDuplicateCandidate;
        var hasSelection = SelectedCandidate is not null;
        OpenSelectedItemButton.IsEnabled = hasSelection;
        MergeSelectedButton.IsEnabled = hasSelection && Review.CanMerge;
        SelectionStatusText.Text = !hasSelection
            ? "비교할 후보 행을 선택하세요."
            : Review.CanMerge
                ? "선택한 후보를 대표 품목으로 사용할 수 있습니다. 병합 전 마지막 확인과 변경감지를 다시 수행합니다."
                : Review.BlockingReasonText;
    }

    private void OpenSelectedItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            MessageBox.Show(this, "원본 화면에서 확인할 품목 후보를 먼저 선택하세요.", "품목 중복 후보 비교", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RequestedItemId = SelectedCandidate.ItemId;
        DialogResult = false;
    }

    private void MergeSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is null)
        {
            MessageBox.Show(this, "대표로 사용할 품목 후보를 먼저 선택하세요.", "품목 중복 후보 비교", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!Review.CanMerge)
        {
            MessageBox.Show(
                this,
                Review.BlockingReasonText,
                "품목 중복 병합 차단",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SelectedCanonicalItemId = SelectedCandidate.ItemId;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12 && e.Key != Key.Escape)
            return;

        DialogWindowCloseHelper.Close(this);
        e.Handled = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
