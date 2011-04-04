using System.Windows;
using System.Windows.Controls;

namespace ModalDialogHostedEditor
{
    public partial class MyControl : UserControl
    {
        public MyControl()
        {
            InitializeComponent();
        }

        private void OnLaunchModalDialog(object sender, RoutedEventArgs e)
        {
            using (MyModalDialog modalDialog = new MyModalDialog())
            {
                modalDialog.ShowModalWithEditorHooked();
            }
        }
    }
}