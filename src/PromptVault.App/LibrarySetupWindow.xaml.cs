using System.Windows;

namespace PromptVault.App;

public partial class LibrarySetupWindow : Window
{
    public LibrarySetupWindow()
    {
        InitializeComponent();
        PathBox.Text = "";
    }

    public string LibraryRoot => Path.GetFullPath(Environment.ExpandEnvironmentVariables(PathBox.Text.Trim()));

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        var initialDirectory = string.IsNullOrWhiteSpace(PathBox.Text)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : PathBox.Text;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择 FR_Imageprompt 图库文件夹", InitialDirectory = initialDirectory };
        if (dialog.ShowDialog(this) == true) PathBox.Text = dialog.FolderName;
    }

    private void StartClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PathBox.Text)) throw new InvalidDataException("请选择图库位置。");
            Directory.CreateDirectory(LibraryRoot);
            DialogResult = true;
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法创建图库", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
