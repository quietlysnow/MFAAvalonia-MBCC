using Avalonia.Controls;
using MFAAvalonia.Helper;

namespace MFAAvalonia.Views.Pages;

public partial class GachaRecordView : UserControl
{
    public GachaRecordView()
    {
        DataContext = Instances.GachaRecordViewModel;
        InitializeComponent();
    }
}
