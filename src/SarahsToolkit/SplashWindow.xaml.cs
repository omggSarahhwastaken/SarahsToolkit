using System;
using System.IO;
using System.Windows;

namespace SarahsToolkit
{
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => PlayVideo();
        }

        private void PlayVideo()
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Assets", "startup.mp4");
                if (!File.Exists(path)) return;
                SplashVideo.Source = new Uri(path);
                // 2x so the draw-in finishes in ~5s, inside a normal launch.
                SplashVideo.SpeedRatio = 2.0;
                SplashVideo.Play();
            }
            catch
            {
                // The static logo underneath stays visible.
                SplashVideo.Visibility = Visibility.Collapsed;
            }
        }

        private void SplashVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Codec missing or file unreadable: fall back to the static logo.
            SplashVideo.Visibility = Visibility.Collapsed;
        }
    }
}
