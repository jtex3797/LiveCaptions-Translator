// Modified by jtex3797 (2026-11): sentence pair rendering, stabilized live slot, animations. Derived from SakiRinn/LiveCaptions-Translator (Apache-2.0).
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Wpf.Ui.Controls;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.Utils;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using Button = Wpf.Ui.Controls.Button;
using Color = System.Windows.Media.Color;
using ColorEnum = LiveCaptionsTranslator.Utils.Color;

namespace LiveCaptionsTranslator
{
    public partial class OverlayWindow : Window
    {
        private readonly Dictionary<ColorEnum, SolidColorBrush> colorMap = new ()
        {
            {ColorEnum.White, Brushes.White},
            {ColorEnum.Yellow, Brushes.Yellow},
            {ColorEnum.LimeGreen, Brushes.LimeGreen},
            {ColorEnum.Aqua, Brushes.Aqua},
            {ColorEnum.Blue, Brushes.Blue},
            {ColorEnum.DeepPink, Brushes.DeepPink},
            {ColorEnum.Red, Brushes.Red},
            {ColorEnum.Black, Brushes.Black},
        };
        private CaptionVisible onlyMode = CaptionVisible.Both;

        // The caption is re-rendered at most once per `RENDER_INTERVAL_MS`, and only when its content changed,
        // so that fast caption revisions do not make the overlay flicker.
        private const int RENDER_INTERVAL_MS = 250;
        private const int LIVE_LINES = 2;
        private const int MASK_K = 2;
        private string shownLive = string.Empty;
        private readonly DispatcherTimer renderTimer = new() { Interval = TimeSpan.FromMilliseconds(RENDER_INTERVAL_MS) };
        private volatile bool renderDirty = true;
        private string lastCommittedKey = string.Empty;
        private string lastLiveTranslationKey = string.Empty;
        private string lastLiveOriginalKey = string.Empty;
        private readonly TranslateTransform committedShift = new();

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                onlyMode = value;
                ResizeForOnlyMode();
            }
        }
        public CaptionLocation SwitchMode { get; set; } = CaptionLocation.TranslationTop;

        public OverlayWindow()
        {
            InitializeComponent();
            DataContext = Translator.Caption;
            TranslatedCaptionCard.RenderTransform = committedShift;
            TranslatedCaptionCard.SizeChanged += (_, _) => AnchorCommittedToBottom();
            CommittedHost.SizeChanged += (_, _) => AnchorCommittedToBottom();

            renderTimer.Tick += (s, e) =>
            {
                if (!renderDirty)
                    return;
                renderDirty = false;
                RenderCaption();
            };
            Loaded += (s, e) =>
            {
                Translator.Caption.PropertyChanged += TranslatedChanged;
                renderTimer.Start();
            };
            Unloaded += (s, e) =>
            {
                Translator.Caption.PropertyChanged -= TranslatedChanged;
                renderTimer.Stop();
            };

            OriginalCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold == Utils.FontBold.Both ?
                FontWeights.Bold : FontWeights.Regular;
            TranslatedCaption.FontWeight = Translator.Setting.OverlayWindow.FontBold >= Utils.FontBold.TranslationOnly ?
                FontWeights.Bold : FontWeights.Regular;

            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            LiveTranslationDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;

            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);

            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];
            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;

            ApplyFontSize();
            ApplyBackgroundOpacity();
        }

        // A bottom-aligned element taller than its slot overflows downward in WPF, which would hide the newest
        // sentence. Anchor with a (possibly negative) top margin instead so the oldest lines are the ones cut off.
        private void AnchorCommittedToBottom()
        {
            double top = CommittedHost.ActualHeight - TranslatedCaptionCard.ActualHeight - 2;
            if (Math.Abs(TranslatedCaptionCard.Margin.Top - top) > 0.5)
                TranslatedCaptionCard.Margin = new Thickness(5, top, 5, 0);
        }

        private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void TopThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height - e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Top += e.VerticalChange;
                this.Height = newHeight;
            }
        }

        private void BottomThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newHeight = this.Height + e.VerticalChange;

            if (newHeight >= this.MinHeight)
            {
                this.Height = newHeight;
            }
        }

        private void LeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width - e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Left += e.HorizontalChange;
                this.Width = newWidth;
            }
        }

        private void RightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = this.Width + e.HorizontalChange;

            if (newWidth >= this.MinWidth)
            {
                this.Width = newWidth;
            }
        }

        private void TopLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void TopRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            TopThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void BottomLeftThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            LeftThumb_OnDragDelta(sender, e);
        }

        private void BottomRightThumb_OnDragDelta(object sender, DragDeltaEventArgs e)
        {
            BottomThumb_OnDragDelta(sender, e);
            RightThumb_OnDragDelta(sender, e);
        }

        private void TranslatedChanged(object sender, PropertyChangedEventArgs e)
        {
            renderDirty = true;
        }

        private void Window_MouseEnter(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Visible;
        }

        private void Window_MouseLeave(object sender, MouseEventArgs e)
        {
            ControlPanel.Visibility = Visibility.Hidden;
        }

        private void FontIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize + StyleConsts.DELTA_FONT_SIZE < StyleConsts.MAX_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize += StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Translator.Setting.OverlayWindow.FontSize -= StyleConsts.DELTA_FONT_SIZE;
                ApplyFontSize();
            }
        }

        private void FontBold_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontBold++;
            if (Translator.Setting.OverlayWindow.FontBold > Utils.FontBold.Both)
                Translator.Setting.OverlayWindow.FontBold = Utils.FontBold.None;
            switch (Translator.Setting.OverlayWindow.FontBold)
            {
                case Utils.FontBold.None:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.TranslationOnly:
                    OriginalCaption.FontWeight = FontWeights.Regular;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
                case Utils.FontBold.SubtitleOnly:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Regular;
                    break;
                case Utils.FontBold.Both:
                    OriginalCaption.FontWeight = FontWeights.Bold;
                    TranslatedCaption.FontWeight = FontWeights.Bold;
                    break;
            }
            renderDirty = true;
        }

        private void FontStrokeIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke += StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontStrokeDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Translator.Setting.OverlayWindow.FontStroke -= StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.FontColor++;
            if (Translator.Setting.OverlayWindow.FontColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.FontColor = ColorEnum.White;
            OriginalCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            TranslatedCaption.Foreground = colorMap[Translator.Setting.OverlayWindow.FontColor];
            UpdateTranslationColor(colorMap[Translator.Setting.OverlayWindow.FontColor]);
        }

        private void BackgroundOpacityIncrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity + StyleConsts.DELTA_OPACITY < StyleConsts.MAX_OPACITY)
                Translator.Setting.OverlayWindow.Opacity += StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MAX_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundOpacityDecrease_Click(object sender, RoutedEventArgs e)
        {
            if (Translator.Setting.OverlayWindow.Opacity - StyleConsts.DELTA_OPACITY > StyleConsts.MIN_OPACITY)
                Translator.Setting.OverlayWindow.Opacity -= StyleConsts.DELTA_OPACITY;
            else
                Translator.Setting.OverlayWindow.Opacity = StyleConsts.MIN_OPACITY;
            ApplyBackgroundOpacity();
        }

        private void BackgroundColorCycle_Click(object sender, RoutedEventArgs e)
        {
            Translator.Setting.OverlayWindow.BackgroundColor++;
            if (Translator.Setting.OverlayWindow.BackgroundColor > ColorEnum.Black)
                Translator.Setting.OverlayWindow.BackgroundColor = ColorEnum.White;
            BorderBackground.Background = colorMap[Translator.Setting.OverlayWindow.BackgroundColor];

            BorderBackground.Opacity = Translator.Setting.OverlayWindow.Opacity;
            ApplyBackgroundOpacity();
        }

        private void OnlyModeButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var symbolIcon = button?.Icon as SymbolIcon;

            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (0) Subtitle + Translation
                symbolIcon.Symbol = SymbolRegular.PanelBottom20;
                OnlyMode = CaptionVisible.Both;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // (1) Translation Only
                symbolIcon.Symbol = SymbolRegular.PanelTopExpand20;
                OnlyMode = CaptionVisible.TranslationOnly;
            }
            else
            {
                // (2) Subtitle Only
                symbolIcon.Symbol = SymbolRegular.PanelTopContract20;
                OnlyMode = CaptionVisible.SubtitleOnly;
            }
        }

        private void SwitchModeButton_Click(object sender, RoutedEventArgs e)
        {
            SwitchMode = SwitchMode == CaptionLocation.TranslationTop ?
                CaptionLocation.SubtitleTop : CaptionLocation.TranslationTop;

            // The live slot follows the same order (translation first or original first).
            LiveStack.Children.Clear();
            if (SwitchMode == CaptionLocation.TranslationTop)
            {
                LiveStack.Children.Add(LiveTranslationDecorator);
                LiveStack.Children.Add(OriginalCaptionDecorator);
            }
            else
            {
                LiveStack.Children.Add(OriginalCaptionDecorator);
                LiveStack.Children.Add(LiveTranslationDecorator);
            }
            renderDirty = true;
        }

        private void ClickThrough_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var extendedStyle = WindowsAPI.GetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE);
            WindowsAPI.SetWindowLong(hwnd, WindowsAPI.GWL_EXSTYLE, extendedStyle | WindowsAPI.WS_EX_TRANSPARENT);
            ControlPanel.Visibility = Visibility.Collapsed;
        }

        public void ResizeForOnlyMode()
        {
            // The window keeps its size (see the minimum height set in RenderCaption), so only the content is rebuilt.
            renderDirty = true;
        }

        public void ApplyFontSize()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                OriginalCaption.FontSize = Translator.Setting.OverlayWindow.FontSize;
                TranslatedCaption.FontSize = (int)(OriginalCaption.FontSize * 1.25);
                renderDirty = true;
            }), DispatcherPriority.Background);
        }

        public void ApplyFontStroke()
        {
            OriginalCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            TranslatedCaptionDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
            LiveTranslationDecorator.StrokeThickness = Translator.Setting.OverlayWindow.FontStroke;
        }

        public void ApplyBackgroundOpacity()
        {
            Color color = ((SolidColorBrush)BorderBackground.Background).Color;
            BorderBackground.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Translator.Setting.OverlayWindow.Opacity, color.R, color.G, color.B));
        }

        private void UpdateTranslationColor(SolidColorBrush brush)
        {
            renderDirty = true;
        }

        private static string StripNotice(string text)
        {
            return RegexPatterns.NoticePrefix().Replace(text ?? string.Empty, string.Empty).Trim();
        }

        private static string Normalize(string text)
        {
            return string.Join(" ", Words(text));
        }

        private static List<string> Words(string text)
        {
            return (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        private static bool EndsSentence(string text)
        {
            text = (text ?? string.Empty).TrimEnd();
            return text.Length > 0 && Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
        }

        private static bool IsSameSentence(string a, string b)
        {
            if (a.Contains(b) || b.Contains(a))
                return true;
            int len = Math.Min(a.Length, b.Length);
            return len >= 15 && TextUtil.Similarity(a.Substring(0, len), b.Substring(0, len)) > TextUtil.SIM_THRESHOLD;
        }

        // Ignores small shrinks of the unstable tail (the recognizer often takes a word back and then re-adds it).
        private static string StabilizeLive(string shown, string candidate)
        {
            var shownWords = Words(shown);
            var candidateWords = Words(candidate);
            int shrink = shownWords.Count - candidateWords.Count;
            if (candidateWords.Count > 0 && shrink > 0 && shrink <= MASK_K + 1)
            {
                int stableCount = Math.Max(0, candidateWords.Count - MASK_K);
                if (candidateWords.Take(stableCount).SequenceEqual(shownWords.Take(stableCount)))
                    return shown;
            }
            return string.Join(" ", candidateWords);
        }

        private double LiveWidth => LiveStack.ActualWidth > 40 ? LiveStack.ActualWidth : 560;

        private FormattedText MeasureText(string text, double size)
        {
            var typeface = new Typeface(TranslatedCaption.FontFamily, FontStyles.Normal, FontWeights.Regular,
                FontStretches.Normal);
            return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size,
                Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip) { MaxTextWidth = LiveWidth };
        }

        // Keeps only the newest words that fit in `maxLines` lines; returns them with a flag for dropped older words.
        private (List<string> Words, bool Trimmed) FitTail(string text, double size, int maxLines)
        {
            var words = Words(text);
            bool trimmed = false;
            if (words.Count > 70)
            {
                words.RemoveRange(0, words.Count - 70);
                trimmed = true;
            }
            if (words.Count == 0)
                return (words, false);

            double limit = MeasureText("Ag", size).Height * maxLines + 1;
            while (words.Count > 1 && MeasureText((trimmed ? "... " : "") + string.Join(" ", words), size).Height > limit)
            {
                words.RemoveAt(0);
                trimmed = true;
            }
            return (words, trimmed);
        }

        private static void ApplyRuns(System.Windows.Controls.TextBlock block,
            IEnumerable<(string Text, double Size, FontWeight Weight, double Opacity, string? Family)> segments,
            Color color)
        {
            block.Inlines.Clear();
            foreach (var seg in segments)
            {
                var run = new Run(seg.Text)
                {
                    FontSize = seg.Size,
                    FontWeight = seg.Weight,
                    FontStyle = FontStyles.Normal,
                    Foreground = new SolidColorBrush(color) { Opacity = seg.Opacity },
                };
                if (seg.Family != null)
                    run.FontFamily = new FontFamily(seg.Family);
                block.Inlines.Add(run);
            }
        }

        private static string SegmentsKey(
            IEnumerable<(string Text, double Size, FontWeight Weight, double Opacity, string? Family)> segments,
            Color color)
        {
            return string.Join("\u0001", segments.Select(
                seg => $"{seg.Text}|{seg.Size}|{seg.Weight}|{seg.Opacity}|{seg.Family}")) + $"|{color}";
        }

        /// <summary>
        /// Overlay layout, top to bottom:
        /// (1) finished sentences, one translation/original pair each, anchored to the bottom of their area and
        ///     sliding up when a sentence is added;
        /// (2) a live slot of fixed height for the sentence being spoken: its provisional translation (2 lines) and
        ///     its original text (2 lines, the least stable last words drawn fainter).
        /// </summary>
        private void RenderCaption()
        {
            var caption = Translator.Caption;
            var color = colorMap[Translator.Setting.OverlayWindow.FontColor].Color;

            bool showTranslation = onlyMode != CaptionVisible.SubtitleOnly;
            bool showOriginal = onlyMode != CaptionVisible.TranslationOnly;
            bool originalFirst = SwitchMode == CaptionLocation.SubtitleTop;

            double translationSize = TranslatedCaption.FontSize;
            double originalSize = Math.Max(10, Math.Round(OriginalCaption.FontSize * 0.85));
            var translationWeight = TranslatedCaption.FontWeight;
            var originalWeight = OriginalCaption.FontWeight;

            // ---------------- (1) finished sentences ----------------
            var segments = new List<(string Text, double Size, FontWeight Weight, double Opacity, string? Family)>();

            void AddPair(string translation, string original, double translationOpacity, double originalOpacity,
                string prefix = "")
            {
                translation = translation.Trim();
                original = original.Trim();
                bool hasTranslation = showTranslation && translation.Length > 0;
                bool hasOriginal = showOriginal && original.Length > 0;
                if (!hasTranslation && !hasOriginal)
                    return;

                void AddTranslation()
                {
                    if (!hasTranslation)
                        return;
                    if (prefix.Length > 0)
                        segments.Add((prefix + " ", translationSize * 0.7, FontWeights.Regular,
                            translationOpacity, "Consolas"));
                    segments.Add((translation + "\n", translationSize, translationWeight, translationOpacity, null));
                }
                void AddOriginal()
                {
                    if (hasOriginal)
                        segments.Add((original + "\n", originalSize, originalWeight, originalOpacity, null));
                }

                if (originalFirst)
                {
                    AddOriginal();
                    AddTranslation();
                }
                else
                {
                    AddTranslation();
                    AddOriginal();
                }
                // Small gap between sentences.
                segments.Add((" \n", 6, FontWeights.Regular, 0, null));
            }

            foreach (var entry in caption.GetPreviousContexts(Translator.Setting.DisplaySentences))
                AddPair(StripNotice(entry.TranslatedText), entry.SourceText ?? string.Empty, 0.78, 0.55);

            string currentTranslation = (caption.OverlayCurrentTranslation ?? string.Empty).Trim();
            string notice = (caption.OverlayNoticePrefix ?? string.Empty).Trim();
            if (currentTranslation.Length == 0 && notice.Length > 0)
            {
                currentTranslation = notice;
                notice = string.Empty;
            }
            string currentOriginal = (caption.OverlayCurrentOriginal ?? string.Empty).Trim();
            string live = Normalize(caption.OverlayLiveOriginal ?? string.Empty);
            string currentKey = Normalize(currentOriginal);

            bool liveIsCurrent = live.Length > 0 && currentKey.Length > 0 && IsSameSentence(live, currentKey);
            bool currentFinal = EndsSentence(currentOriginal);

            // A translation made from an unfinished sentence (the caption paused mid-sentence) is not shown here;
            // the live slot shows that sentence instead, until it is finished.
            if (!liveIsCurrent || currentFinal)
                AddPair(currentTranslation, currentOriginal, 1.0, 0.7, notice);

            while (segments.Count > 0 && segments[^1].Text.Trim().Length == 0)
                segments.RemoveAt(segments.Count - 1);
            if (segments.Count > 0)
            {
                var last = segments[^1];
                segments[^1] = (last.Text.TrimEnd('\n'), last.Size, last.Weight, last.Opacity, last.Family);
            }

            string committedKey = SegmentsKey(segments, color);
            if (committedKey != lastCommittedKey)
            {
                lastCommittedKey = committedKey;
                double oldHeight = TranslatedCaption.ActualHeight;
                ApplyRuns(TranslatedCaption, segments, color);
                TranslatedCaptionDecorator.InvalidateVisual();

                // The block is bottom-anchored, so a taller block pushes the older lines up at once.
                // Start from the old position and glide to the new one.
                TranslatedCaptionCard.UpdateLayout();
                double delta = TranslatedCaption.ActualHeight - oldHeight;
                if (oldHeight > 0 && Math.Abs(delta) > 2)
                {
                    committedShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(delta, 0,
                        TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                }
            }

            // ---------------- (2) live slot ----------------
            bool liveActive = live.Length > 0 && !(liveIsCurrent && currentFinal);
            shownLive = StabilizeLive(shownLive, liveActive ? live : string.Empty);

            LiveTranslationDecorator.Visibility = showTranslation ? Visibility.Visible : Visibility.Collapsed;
            OriginalCaptionDecorator.Visibility = showOriginal ? Visibility.Visible : Visibility.Collapsed;
            LiveTranslation.Height = Math.Ceiling(MeasureText("Ag", translationSize).Height * LIVE_LINES) + 2;
            OriginalCaption.Height = Math.Ceiling(MeasureText("Ag", originalSize).Height * LIVE_LINES) + 2;

            // The window must always have room for the whole live slot plus at least one finished pair,
            // otherwise a small saved size cuts the live slot and the hover buttons cover it.
            const double CARD_CHROME = 20;      // card padding 8+8 and margin 2+2
            const double BORDER_CHROME = 10;    // outer border margin 5+5
            double liveHeight = (showTranslation ? LiveTranslation.Height : 0) + (showOriginal ? OriginalCaption.Height : 0);
            double pairHeight = (showTranslation ? MeasureText("Ag", translationSize).Height : 0) +
                                (showOriginal ? MeasureText("Ag", originalSize).Height : 0);
            double wantedMinHeight = Math.Ceiling(liveHeight + CARD_CHROME + pairHeight + CARD_CHROME + BORDER_CHROME);
            if (Math.Abs(MinHeight - wantedMinHeight) > 1)
                MinHeight = Math.Max(135, wantedMinHeight);

            // Provisional translation: only if it was made from the beginning of this very sentence.
            var translationSegments = new List<(string Text, double Size, FontWeight Weight, double Opacity, string? Family)>();
            if (showTranslation && shownLive.Length > 0)
            {
                string source = Normalize(caption.OverlayLiveTranslationSource ?? string.Empty);
                string provisional = StripNotice(caption.OverlayLiveTranslation ?? string.Empty);
                if (provisional.Length > 0 && source.Length > 0 && IsSameSentence(shownLive, source))
                {
                    var (words, trimmed) = FitTail(provisional, translationSize, LIVE_LINES);
                    translationSegments.Add(((trimmed ? "... " : "") + string.Join(" ", words), translationSize,
                        translationWeight, 0.7, null));
                }
            }
            string liveTranslationKey = SegmentsKey(translationSegments, color);
            if (liveTranslationKey != lastLiveTranslationKey)
            {
                bool hadText = lastLiveTranslationKey.Length > 0 && !lastLiveTranslationKey.StartsWith("|");
                lastLiveTranslationKey = liveTranslationKey;
                ApplyRuns(LiveTranslation, translationSegments, color);
                LiveTranslationDecorator.InvalidateVisual();
                if (translationSegments.Count > 0)
                {
                    // Soft fade-in instead of a hard swap when the translation is replaced.
                    LiveTranslationDecorator.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(
                        hadText ? 0.45 : 0.0, 1, TimeSpan.FromMilliseconds(260))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                }
            }

            // Original text of the sentence being spoken; the last MASK_K words are the least stable.
            var originalSegments = new List<(string Text, double Size, FontWeight Weight, double Opacity, string? Family)>();
            if (showOriginal && shownLive.Length > 0)
            {
                var (words, trimmed) = FitTail(shownLive, originalSize, LIVE_LINES);
                int tailCount = Math.Min(MASK_K, words.Count);
                string stable = (trimmed ? "... " : "") + string.Join(" ", words.Take(words.Count - tailCount));
                if (words.Count > tailCount)
                    stable += " ";
                else if (trimmed)
                    stable = "... ";
                if (stable.Trim().Length > 0)
                    originalSegments.Add((stable, originalSize, FontWeights.Regular, 0.5, null));
                originalSegments.Add((string.Join(" ", words.Skip(words.Count - tailCount)), originalSize,
                    FontWeights.Regular, 0.28, null));
            }
            string liveOriginalKey = SegmentsKey(originalSegments, color);
            if (liveOriginalKey != lastLiveOriginalKey)
            {
                lastLiveOriginalKey = liveOriginalKey;
                ApplyRuns(OriginalCaption, originalSegments, color);
                OriginalCaptionDecorator.InvalidateVisual();
            }
        }
    }
}
