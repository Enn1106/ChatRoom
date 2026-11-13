using System.Windows;

namespace ChatClientWpf;

/// <summary>Hộp thoại nhập một dòng chữ (tên phòng, tên tài khoản...).</summary>
public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputBox.Text = initial;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Hiện hộp thoại; trả về chuỗi đã nhập, hoặc null nếu người dùng hủy hoặc để trống.</summary>
    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        if (dialog.ShowDialog() != true) return null;
        var value = dialog.InputBox.Text.Trim();
        return value.Length == 0 ? null : value;
    }
}
