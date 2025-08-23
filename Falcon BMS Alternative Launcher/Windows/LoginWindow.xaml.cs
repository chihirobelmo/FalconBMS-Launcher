using System;
using System.Threading.Tasks;
using System.Windows;

namespace FalconBMS.Launcher.Windows
{
    public partial class LoginWindow : MahApps.Metro.Controls.MetroWindow
    {
        public LoginWindow()
        {
            InitializeComponent();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private async void Login_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoginButton.IsEnabled = false;
                StatusText.Text = "Signing in...";
                var ok = await ApiSession.Instance.LoginAsync(EmailBox.Text, PasswordBox.Password, RememberMeBox.IsChecked == true);
                if (ok)
                {
                    StatusText.Text = "Success";
                    DialogResult = true;
                    Close();
                }
                else
                {
                    StatusText.Text = "Failed";
                    LoginButton.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
                LoginButton.IsEnabled = true;
            }
        }
    }
}
