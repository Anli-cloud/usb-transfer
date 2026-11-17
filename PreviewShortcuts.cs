using System;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace UsbTransfer
{
    internal sealed class PreviewShortcuts
    {
        private const double SkipSeconds = 5;
        private const double HoldSpeed = 2;
        private readonly MediaElement media;
        private readonly Func<bool> canSeek;
        private readonly Func<bool> isPlaying;
        private readonly Func<double> position;
        private readonly Action<double> seek;
        private readonly Action<double> changeVolume;
        private readonly Action<string> showRate;
        private readonly DispatcherTimer holdDelay;
        private readonly DispatcherTimer rewindTimer;
        private readonly Stopwatch rewindClock = new Stopwatch();
        private Key heldKey;
        private int direction;
        private bool accelerated;
        private double rewindStart;

        internal PreviewShortcuts(MediaElement media, Func<bool> canSeek, Func<bool> isPlaying, Func<double> position,
            Action<double> seek, Action<double> changeVolume, Action<string> showRate)
        {
            this.media = media;
            this.canSeek = canSeek;
            this.isPlaying = isPlaying;
            this.position = position;
            this.seek = seek;
            this.changeVolume = changeVolume;
            this.showRate = showRate;
            holdDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            holdDelay.Tick += delegate { StartFastSeek(); };
            rewindTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            rewindTimer.Tick += delegate { UpdateRewind(); };
        }

        internal void OnKeyDown(object sender, KeyEventArgs args)
        {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
            switch (args.Key)
            {
                case Key.Up:
                case Key.W:
                    changeVolume(1);
                    break;
                case Key.Down:
                case Key.S:
                    changeVolume(-1);
                    break;
                case Key.Left:
                case Key.A:
                    Begin(args.Key, -1, args.IsRepeat);
                    break;
                case Key.Right:
                case Key.D:
                    Begin(args.Key, 1, args.IsRepeat);
                    break;
                default:
                    return;
            }
            args.Handled = true;
        }

        internal void OnKeyUp(object sender, KeyEventArgs args)
        {
            if (heldKey == Key.None || args.Key != heldKey) return;
            args.Handled = true;
            if (canSeek())
            {
                if (!accelerated)
                    seek(position() + direction * SkipSeconds);
                else if (direction < 0)
                    UpdateRewind();
            }
            Cancel();
        }

        internal void Cancel()
        {
            holdDelay.Stop();
            rewindTimer.Stop();
            rewindClock.Reset();
            heldKey = Key.None;
            if (!accelerated) return;
            accelerated = false;
            media.SpeedRatio = 1;
            showRate(null);
            if (!canSeek()) return;
            if (isPlaying()) media.Play();
            else media.Pause();
        }

        private void Begin(Key key, int seekDirection, bool isRepeat)
        {
            if (isRepeat || heldKey == key || !canSeek()) return;
            Cancel();
            heldKey = key;
            direction = seekDirection;
            holdDelay.Start();
        }

        private void StartFastSeek()
        {
            holdDelay.Stop();
            if (heldKey == Key.None || !canSeek()) return;
            accelerated = true;
            if (direction > 0)
            {
                media.SpeedRatio = HoldSpeed;
                media.Play();
                showRate("2× 快进");
                return;
            }
            rewindStart = position();
            media.Pause();
            rewindClock.Restart();
            rewindTimer.Start();
            showRate("2× 快退");
        }

        private void UpdateRewind()
        {
            double target = Math.Max(0, rewindStart - rewindClock.Elapsed.TotalSeconds * HoldSpeed);
            seek(target);
            if (target == 0) rewindTimer.Stop();
        }
    }
}
