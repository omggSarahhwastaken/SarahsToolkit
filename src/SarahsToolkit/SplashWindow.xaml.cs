using System;
using System.IO;
using System.Windows;

namespace SarahsToolkit
{
    public partial class SplashWindow : Window
    {
        /// <summary>Raised when the video finished playing, or when it
        /// can't play at all (missing file, missing codec).</summary>
        public event EventHandler VideoFinished;

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
                if (!File.Exists(path))
                {
                    // No video to play: don't hold the splash for it.
                    VideoFinished?.Invoke(this, EventArgs.Empty);
                    return;
                }
                SplashVideo.Source = new Uri(path);
                // 2x so the draw-in finishes in ~5s, inside a normal launch.
                SplashVideo.SpeedRatio = 2.0;
                // Play() is called from MediaOpened: with Manual behaviour
                // calling it before the media opens can be a no-op.
            }
            catch
            {
                // The static logo underneath stays visible.
                SplashVideo.Visibility = Visibility.Collapsed;
                VideoFinished?.Invoke(this, EventArgs.Empty);
            }
        }

        private void SplashVideo_MediaOpened(object sender, RoutedEventArgs e)
        {
            SplashVideo.Play();
        }

        private void SplashVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            VideoFinished?.Invoke(this, EventArgs.Empty);
        }

        private void SplashVideo_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            // Codec missing or file unreadable: fall back to the static logo.
            SplashVideo.Visibility = Visibility.Collapsed;
            VideoFinished?.Invoke(this, EventArgs.Empty);
        }
    }
}
