using System;
using System.Windows;

namespace SarahsToolkit
{
    public partial class SplashWindow : Window
    {
        /// <summary>Raised when the draw-in animation has played through.</summary>
        public event EventHandler AnimationFinished;

        public SplashWindow()
        {
            InitializeComponent();
            // CenterScreen centers on the monitor with the mouse cursor —
            // the splash followed the mouse to the other monitor. Center
            // on the primary monitor explicitly: all windows start on
            // the main monitor. SystemParameters is in DIPs and always
            // refers to the primary display.
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        }

        private void DrawInStoryboard_Completed(object sender, EventArgs e)
        {
            AnimationFinished?.Invoke(this, EventArgs.Empty);
        }
    }
}
