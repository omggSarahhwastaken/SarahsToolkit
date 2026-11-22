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
        }

        private void DrawInStoryboard_Completed(object sender, EventArgs e)
        {
            AnimationFinished?.Invoke(this, EventArgs.Empty);
        }
    }
}
