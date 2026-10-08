/* *******************************************************************************************************************
 * Application: ClaudeCodeExtension
 *
 * Autor:  Daniel Carvalho Liedke / Claude Code
 *
 * Copyright © Daniel Carvalho Liedke 2026
 * Usage and reproduction in any manner whatsoever without the written permission of Daniel Carvalho Liedke is strictly forbidden.
 *
 * Purpose: Searchable model picker window for Devin, modeled on Devin Desktop's own picker: a search
 *          box, Adaptive pinned on top, starred favorites, then every family, with a details pane
 *          showing the context window and the per-1M prices Devin reports, on a cost bar.
 *
 * *******************************************************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

using ClaudeCodeVS.Agents;

using Microsoft.VisualStudio.Shell;

namespace ClaudeCodeVS
{
    public partial class ClaudeCodeControl
    {
        #region Model Picker

        /// <summary>
        /// Providers whose model menu opens the picker window instead of listing every model inline.
        /// Devin only: it is the one CLI that reports enough per model (family, cost tier, prices,
        /// context window) for a details pane, and the one whose list — 158 models in 31 families —
        /// is unusable as nested submenus.
        /// </summary>
        private static bool ProviderUsesModelPicker(AiProvider? provider)
        {
            return IsDevinProvider(provider);
        }

        /// <summary>The Devin models starred as favorites, most recently starred first. Never null.</summary>
        private List<string> GetFavoriteDevinModelIds()
        {
            if (_settings?.FavoriteDevinModels == null) return new List<string>();

            return new List<string>(_settings.FavoriteDevinModels);
        }

        /// <summary>
        /// Stars or unstars a model so it shows up under (or drops out of) "Favorites" — in both the
        /// picker and the model menu — next time. Saves immediately: a parallel chat tab's model
        /// picker never touches the settings file otherwise, since its model choice is session-only.
        /// </summary>
        private bool ToggleFavoriteDevinModel(string modelId)
        {
            if (_settings == null || string.IsNullOrWhiteSpace(modelId)) return false;

            _settings.FavoriteDevinModels = ModelPickerView.ToggleFavorite(_settings.FavoriteDevinModels, modelId);
            SaveSettings();

            return ModelPickerView.IsFavorite(_settings.FavoriteDevinModels, modelId);
        }

        /// <summary>
        /// Shows the picker and returns what the user chose: a model id, an empty string for the
        /// agent's own default, or null when the window was cancelled. Does not apply the choice —
        /// the caller routes it into the panel or the chat tab, which differ in how they switch.
        /// </summary>
        private string ShowProviderModelPickerDialog(AiProvider provider, string selectedId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            GetThemeBrushes(out Brush themeBg, out Brush themeFg);

            string chosen = selectedId ?? string.Empty;
            string result = null;

            // What the details pane and a rebuild (search, refresh, star toggle) land on. Starts on
            // the model already in use, then follows whatever the user has highlighted since, so
            // starring a model does not fling the list back to the original selection.
            string highlighted = chosen;

            // The price the bar shows. It survives moving through the list, so one price can be compared across models.
            string costMetric = "Input";

            var dialog = new Window
            {
                Title = "Select Model — " + GetProviderDisplayName(provider),
                Width = 860,
                Height = 620,
                MinWidth = 680,
                MinHeight = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                Background = themeBg,
                Foreground = themeFg,
                ShowInTaskbar = false
            };
            try { dialog.Owner = Application.Current?.MainWindow; } catch { }

            var rootGrid = new Grid { Margin = new Thickness(12) };
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                   // search
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // list + details
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                   // buttons

            // ---- Row 0: search ----
            var searchBox = new TextBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                Height = 28,
                VerticalContentAlignment = VerticalAlignment.Center,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 0, 0, 8),
                ToolTip = "Type to search every model by name, family or id. Words can be typed in any order."
            };
            Grid.SetRow(searchBox, 0);
            rootGrid.Children.Add(searchBox);

            // Watermark: drawn behind the (transparent-background) text box would need a template, so
            // it simply sits on top of the empty box and hides itself as soon as anything is typed.
            var searchHint = new TextBlock
            {
                Text = "Search all models",
                Foreground = themeFg,
                Opacity = 0.5,
                IsHitTestVisible = false,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 8)
            };
            Grid.SetRow(searchHint, 0);
            rootGrid.Children.Add(searchHint);

            // ---- Row 1: list + details ----
            var contentGrid = new Grid();
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            Grid.SetRow(contentGrid, 1);
            rootGrid.Children.Add(contentGrid);

            var listBox = new ListBox
            {
                Background = themeBg,
                Foreground = themeFg,
                BorderBrush = themeFg,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            ScrollViewer.SetHorizontalScrollBarVisibility(listBox, ScrollBarVisibility.Disabled);
            var rowStyle = new Style(typeof(ListBoxItem));
            rowStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 3, 6, 3)));
            rowStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            listBox.ItemContainerStyle = rowStyle;
            Grid.SetColumn(listBox, 0);
            contentGrid.Children.Add(listBox);

            var detailsPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            var detailsScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = detailsPanel
            };
            Grid.SetColumn(detailsScroll, 1);
            contentGrid.Children.Add(detailsScroll);

            // ---- Row 2: buttons ----
            Style buttonStyle = GetDialogButtonStyle();
            Func<string, Button> makeButton = text =>
            {
                var button = new Button
                {
                    Content = text,
                    MinWidth = 96,
                    Height = 28,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                if (buttonStyle != null) button.Style = buttonStyle;
                else { button.Background = themeBg; button.Foreground = themeFg; button.BorderBrush = themeFg; }
                return button;
            };

            var refreshButton = makeButton("Refresh Models");
            refreshButton.Margin = new Thickness(0);
            refreshButton.ToolTip = "Read the model list from the Devin CLI again.";

            var defaultButton = makeButton("Agent Default");
            defaultButton.ToolTip = "Let Devin start on the model its own account defaults to.";

            var selectButton = makeButton("Select");
            selectButton.IsDefault = true;

            var cancelButton = makeButton("Cancel");
            cancelButton.IsCancel = true;

            // Loading / empty-list message, next to the buttons so the details pane can be cleared
            // and rebuilt without taking it with it.
            var status = new TextBlock
            {
                Foreground = themeFg,
                Opacity = 0.75,
                FontStyle = FontStyles.Italic,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 12, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = Visibility.Collapsed
            };

            var buttonRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(refreshButton, 0);
            buttonRow.Children.Add(refreshButton);

            Grid.SetColumn(status, 1);
            buttonRow.Children.Add(status);

            var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            rightButtons.Children.Add(defaultButton);
            rightButtons.Children.Add(selectButton);
            rightButtons.Children.Add(cancelButton);
            Grid.SetColumn(rightButtons, 2);
            buttonRow.Children.Add(rightButtons);

            Grid.SetRow(buttonRow, 2);
            rootGrid.Children.Add(buttonRow);

            // ---- Details pane ----
            Action<ModelOption> showDetails = null;
            showDetails = model =>
            {
                detailsPanel.Children.Clear();
                if (model == null) return;

                detailsPanel.Children.Add(new TextBlock
                {
                    Text = model.DisplayName,
                    FontSize = 15,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = themeFg,
                    TextWrapping = TextWrapping.Wrap
                });

                Action<string, double> addLine = (text, opacity) =>
                {
                    if (string.IsNullOrWhiteSpace(text)) return;

                    detailsPanel.Children.Add(new TextBlock
                    {
                        Text = text,
                        Foreground = themeFg,
                        Opacity = opacity,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 6, 0, 0)
                    });
                };

                addLine(model.Group, 0.75);
                addLine(model.ContextWindowLabel, 1.0);

                List<ModelOption> catalog = GetCachedProviderModels(provider);
                ModelOption inUse = catalog.FirstOrDefault(option => string.Equals(option.Id, chosen, StringComparison.OrdinalIgnoreCase));
                FrameworkElement cost = BuildCostSection(model, inUse, ModelCostScale.Build(catalog), costMetric, themeFg, metric =>
                {
                    costMetric = metric;
                    showDetails(model);
                });
                if (cost != null) detailsPanel.Children.Add(cost);

                addLine(model.Description, 0.85);

                var badges = new List<string>();
                if (model.IsNew) badges.Add("New");
                if (model.IsBeta) badges.Add("Beta");
                addLine(string.Join(" · ", badges), 1.0);

                addLine(model.Id, 0.55);
            };

            // ---- List building ----
            // Rebuilt from scratch on every keystroke and after a refresh; the list is small enough
            // (158 rows at most) that rebuilding beats maintaining a filtered view.
            bool refreshing = false;

            Action rebuild = null;
            rebuild = () =>
            {
                List<ModelOption> models = GetCachedProviderModels(provider);
                List<string> favoriteIds = GetFavoriteDevinModelIds();
                List<ModelPickerSection> sections =
                    ModelPickerView.Build(models, favoriteIds, searchBox.Text);

                listBox.Items.Clear();
                ListBoxItem toSelect = null;

                foreach (ModelPickerSection section in sections)
                {
                    if (section.HasHeader)
                    {
                        listBox.Items.Add(new ListBoxItem
                        {
                            Content = new TextBlock
                            {
                                Text = section.Name,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = themeFg,
                                Opacity = 0.7,
                                Margin = new Thickness(0, 6, 0, 0)
                            },
                            Focusable = false,
                            IsHitTestVisible = false
                        });
                    }

                    foreach (ModelOption model in section.Models)
                    {
                        bool isFavorite = ModelPickerView.IsFavorite(favoriteIds, model.Id);
                        ListBoxItem row = BuildModelPickerRow(model, chosen, isFavorite, themeFg, toggled =>
                        {
                            highlighted = toggled.Id;
                            bool nowFavorite = ToggleFavoriteDevinModel(toggled.Id);
                            Debug.WriteLine($"Model picker: {toggled.Id} favorite = {nowFavorite}");
                            rebuild();
                        });
                        listBox.Items.Add(row);

                        // Land on whatever is highlighted, so opening the picker and pressing Enter is
                        // a no-op rather than a switch to whatever happened to be first, and a star
                        // click does not lose the user's place in the list.
                        if (toSelect == null && string.Equals(model.Id, highlighted, StringComparison.OrdinalIgnoreCase))
                        {
                            toSelect = row;
                        }
                    }
                }

                if (toSelect == null)
                {
                    foreach (object item in listBox.Items)
                    {
                        var row = item as ListBoxItem;
                        if (row?.Tag is ModelOption) { toSelect = row; break; }
                    }
                }

                if (toSelect != null)
                {
                    listBox.SelectedItem = toSelect;
                    listBox.ScrollIntoView(toSelect);
                }
                else
                {
                    showDetails(null);
                }

                // A refresh in flight owns the status line; otherwise it explains an empty list.
                if (refreshing) return;

                if (listBox.Items.Count > 0)
                {
                    status.Visibility = Visibility.Collapsed;
                    return;
                }

                status.Text = models.Count == 0
                    ? "No models reported by the agent."
                    : "No model matches the search.";
                status.Visibility = Visibility.Visible;
            };

            listBox.SelectionChanged += (s, e) =>
            {
                var row = listBox.SelectedItem as ListBoxItem;
                var model = row?.Tag as ModelOption;
                if (model != null) highlighted = model.Id;
                showDetails(model);
            };

            // Double-click picks, the way a file list does.
            listBox.MouseDoubleClick += (s, e) =>
            {
                var row = listBox.SelectedItem as ListBoxItem;
                if (row?.Tag is ModelOption) dialog.DialogResult = true;
            };

            searchBox.TextChanged += (s, e) =>
            {
                searchHint.Visibility = searchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
                rebuild();
            };

            // Arrow keys move through the results without leaving the search box, so a search can be
            // narrowed and picked in one go.
            searchBox.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != Key.Down && e.Key != Key.Up) return;

                MoveModelPickerSelection(listBox, e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
            };

            selectButton.Click += (s, e) => dialog.DialogResult = true;
            defaultButton.Click += (s, e) =>
            {
                result = string.Empty;
                dialog.DialogResult = true;
            };

            refreshButton.Click += (s, e) =>
            {
                refreshButton.IsEnabled = false;
                refreshing = true;
                status.Text = "Refreshing the model list…";
                status.Visibility = Visibility.Visible;

                StartModelPickerRefresh(provider, () =>
                {
                    refreshButton.IsEnabled = true;
                    refreshing = false;
                    rebuild();
                });
            };

            dialog.Loaded += (s, e) =>
            {
                searchBox.Focus();

                if (!ShouldRefreshProviderModels(provider)) return;

                refreshing = true;
                status.Text = "Loading models…";
                status.Visibility = Visibility.Visible;

                StartModelPickerRefresh(provider, () =>
                {
                    refreshing = false;
                    rebuild();
                });
            };

            rebuild();

            dialog.Content = rootGrid;

            if (dialog.ShowDialog() != true) return null;

            // "Agent Default" already set the result; anything else takes the highlighted row.
            if (result != null) return result;

            var selectedRow = listBox.SelectedItem as ListBoxItem;
            var selectedModel = selectedRow?.Tag as ModelOption;

            return selectedModel?.Id;
        }

        /// <summary>
        /// The star's color when a model is favorited — a fixed gold that reads on both themes,
        /// rather than a theme brush, so a favorite still stands out set against dark or light rows.
        /// </summary>
        private static readonly Brush FavoriteStarBrush = CreateFrozenBrush(0xFF, 0xC1, 0x07);

        private static Brush CreateFrozenBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// One model row: a star toggle, a tick in front of the model currently in use, the caption,
        /// then whatever the CLI reported about the model. The star is its own button so clicking it
        /// toggles the favorite without picking the row.
        /// </summary>
        private static ListBoxItem BuildModelPickerRow(
            ModelOption model, string selected, bool isFavorite, Brush fg, Action<ModelOption> onToggleFavorite)
        {
            bool isSelected = string.Equals(model.Id, selected, StringComparison.OrdinalIgnoreCase);

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var star = new Button
            {
                Content = isFavorite ? "★" : "☆",
                Foreground = isFavorite ? FavoriteStarBrush : fg,
                Opacity = isFavorite ? 1.0 : 0.45,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                ToolTip = isFavorite ? "Remove from favorites" : "Add to favorites"
            };
            star.Click += (s, e) =>
            {
                e.Handled = true;
                onToggleFavorite(model);
            };
            Grid.SetColumn(star, 0);
            row.Children.Add(star);

            var tick = new TextBlock
            {
                Text = isSelected ? "✓" : string.Empty,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tick, 1);
            row.Children.Add(tick);

            var name = new TextBlock
            {
                Text = model.DisplayName,
                Foreground = fg,
                FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 2);
            row.Children.Add(name);

            var details = new List<string>();
            string context = ModelOption.FormatContextWindow(model.ContextTokens);
            if (context.Length > 0) details.Add(context);
            if (!string.IsNullOrWhiteSpace(model.CostTier)) details.Add(model.CostTier.Trim());
            if (model.IsNew) details.Add("New");
            if (model.IsBeta) details.Add("Beta");

            var detailText = new TextBlock
            {
                Text = string.Join(" · ", details),
                Foreground = fg,
                Opacity = 0.65,
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(detailText, 3);
            row.Children.Add(detailText);

            return new ListBoxItem
            {
                Content = row,
                Tag = model,
                ToolTip = model.Id
            };
        }

        /// <summary>
        /// Devin's cost gradient, the same three stops its picker draws: cheapest green, middle orange,
        /// dearest purple. Fixed colours for the same reason as <see cref="FavoriteStarBrush"/>.
        /// </summary>
        private static readonly Brush CostBarBrush = CreateCostBarBrush();

        /// <summary>The track's dots: the model's own, and the dimmer one for the model in use.</summary>
        private static readonly Brush MarkerBrush = Brushes.White;
        private static readonly Brush CurrentMarkerBrush = CreateFrozenBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF));
        private static readonly Brush MarkerRingBrush = CreateFrozenBrush(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

        private const double CostTrackHeight = 8;
        private const double CostMarkerSize = 8;

        private static Brush CreateCostBarBrush()
        {
            var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 165, 88), 0.0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(245, 142, 58), 0.5));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(149, 108, 222), 1.0));
            brush.Freeze();
            return brush;
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>The brush's colour at another alpha: the translucent tints the tiles and the divider use.</summary>
        private static Brush WithAlpha(Brush brush, byte alpha)
        {
            Color color = (brush as SolidColorBrush)?.Color ?? Colors.White;
            return CreateFrozenBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        /// <summary>
        /// The details pane's cost section, laid out as Devin's picker lays it out: a divider, the "Cost" caption,
        /// the bar for the price picked, then one tile per price. The bar marks this model and, when another model
        /// is in use, that model too, captioned "Current". Null when the model reports no prices.
        /// </summary>
        private static FrameworkElement BuildCostSection(
            ModelOption model, ModelOption inUse, Dictionary<string, ModelCostRange> ranges,
            string metric, Brush fg, Action<string> onMetric)
        {
            List<ModelPrice> prices = ModelCatalogParsers.ParseCostSummary(model.CostSummary);
            if (prices.Count == 0)
            {
                if (string.IsNullOrWhiteSpace(model.CostSummary)) return null;

                // A summary the parser does not recognise is shown as the CLI wrote it.
                return new TextBlock
                {
                    Text = model.CostSummary,
                    Foreground = fg,
                    Opacity = 0.85,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 10, 0, 0)
                };
            }

            ModelPrice picked = prices.FirstOrDefault(p => string.Equals(p.Label, metric, StringComparison.OrdinalIgnoreCase)) ?? prices[0];
            ModelCostRange range;
            ranges.TryGetValue(picked.Label, out range);
            double position = ModelCostScale.Percent(picked.Amount, range);

            // The model in use gets a marker of its own, unless it is this model or has no such price.
            ModelPrice usedPrice = null;
            if (inUse != null && !string.Equals(inUse.Id, model.Id, StringComparison.OrdinalIgnoreCase))
            {
                usedPrice = ModelCatalogParsers.ParseCostSummary(inUse.CostSummary)
                    .FirstOrDefault(p => string.Equals(p.Label, picked.Label, StringComparison.OrdinalIgnoreCase));
            }
            double usedPosition = usedPrice == null ? 0 : ModelCostScale.Percent(usedPrice.Amount, range);

            var section = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            section.Children.Add(new Border { Height = 1, Background = WithAlpha(fg, 0x26), Margin = new Thickness(0, 0, 0, 8) });
            section.Children.Add(new TextBlock { Text = "Cost", Foreground = fg, FontSize = 13 });

            var bar = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
            bar.Children.Add(BuildCaptionRow(
                string.IsNullOrWhiteSpace(model.Group) ? model.DisplayName : model.Group, position, fg, new Thickness(0, 0, 0, 4)));

            var track = new Grid { Height = CostTrackHeight };
            track.Children.Add(new Border { Height = CostTrackHeight, CornerRadius = new CornerRadius(CostTrackHeight / 2), Background = CostBarBrush });
            if (usedPrice != null)
            {
                track.Children.Add(BuildMarker(usedPosition, CurrentMarkerBrush, inUse.DisplayName + ": " + usedPrice.Text + " / " + usedPrice.Denominator));
            }
            track.Children.Add(BuildMarker(position, MarkerBrush, picked.Text + " / " + picked.Denominator));
            bar.Children.Add(track);

            if (usedPrice != null)
            {
                bar.Children.Add(BuildCaptionRow("Current", usedPosition, fg, new Thickness(0, 4, 0, 0)));
            }
            section.Children.Add(bar);

            section.Children.Add(BuildPriceTiles(prices, picked.Label, fg, onMetric));
            return section;
        }

        /// <summary>
        /// A caption centred over <paramref name="percent"/> of the track and kept inside the track at either end,
        /// as Devin keeps its captions. Its width is known only after layout, so it is placed on the first size change.
        /// </summary>
        private static FrameworkElement BuildCaptionRow(string text, double percent, Brush fg, Thickness margin)
        {
            var label = new TextBlock { Text = text, Foreground = fg, FontSize = 10, FontWeight = FontWeights.SemiBold };
            var row = new Canvas { Height = 14, Margin = margin };
            row.Children.Add(label);
            row.SizeChanged += (s, e) =>
            {
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                double width = label.DesiredSize.Width;
                double centre = percent / 100 * (row.ActualWidth - CostMarkerSize) + CostMarkerSize / 2;
                Canvas.SetLeft(label, Math.Max(0, Math.Min(centre - width / 2, row.ActualWidth - width)));
            };
            return row;
        }

        /// <summary>
        /// A dot on the track at <paramref name="percent"/>. It has a fixed-width column between two stretch columns,
        /// so its centre lands at that share of the track whatever the pane's width.
        /// </summary>
        private static FrameworkElement BuildMarker(double percent, Brush fill, string tooltip)
        {
            double share = Math.Min(100, Math.Max(0, percent));

            var layer = new Grid();
            layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
            layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CostMarkerSize) });
            layer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - share, GridUnitType.Star) });

            var dot = new Border
            {
                Width = CostMarkerSize,
                Height = CostMarkerSize,
                CornerRadius = new CornerRadius(CostMarkerSize / 2),
                Background = fill,
                BorderBrush = MarkerRingBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = tooltip
            };
            Grid.SetColumn(dot, 1);
            layer.Children.Add(dot);

            return layer;
        }

        /// <summary>
        /// One tile per price: its caption small, the price beside its unit. The tile of the price the bar shows is
        /// filled, and clicking a tile (or pressing Enter or Space on it) moves the bar to that price.
        /// </summary>
        private static FrameworkElement BuildPriceTiles(List<ModelPrice> prices, string pickedLabel, Brush fg, Action<string> onMetric)
        {
            var tiles = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            Brush dim = WithAlpha(fg, 0x99);
            Brush selectedFill = WithAlpha(fg, 0x26);
            Brush hoverFill = WithAlpha(fg, 0x14);

            for (int i = 0; i < prices.Count; i++)
            {
                ModelPrice price = prices[i];
                bool picked = string.Equals(price.Label, pickedLabel, StringComparison.OrdinalIgnoreCase);

                var amount = new TextBlock { Foreground = fg, FontSize = 12, FontWeight = FontWeights.Medium, Margin = new Thickness(0, 2, 0, 0) };
                amount.Inlines.Add(new Run(price.Text));
                amount.Inlines.Add(new Run(" / " + price.Denominator) { FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = dim });

                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = price.Label, Foreground = dim, FontSize = 10, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                content.Children.Add(amount);

                var tile = new Border
                {
                    Background = picked ? selectedFill : Brushes.Transparent,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, i < prices.Count - 1 ? 6 : 0, 0),
                    Cursor = Cursors.Hand,
                    Focusable = true,
                    Child = content
                };
                if (!picked)
                {
                    tile.MouseEnter += (s, e) => tile.Background = hoverFill;
                    tile.MouseLeave += (s, e) => tile.Background = Brushes.Transparent;
                }
                tile.MouseLeftButtonUp += (s, e) => onMetric?.Invoke(price.Label);
                tile.KeyDown += (s, e) =>
                {
                    if (e.Key != Key.Enter && e.Key != Key.Space) return;

                    e.Handled = true;
                    onMetric?.Invoke(price.Label);
                };

                tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(tile, i);
                tiles.Children.Add(tile);
            }

            return tiles;
        }

        /// <summary>
        /// Moves the highlight by one model, stepping over the section headers (which are rows too,
        /// just unselectable ones).
        /// </summary>
        private static void MoveModelPickerSelection(ListBox listBox, int direction)
        {
            int index = listBox.SelectedIndex;

            for (int step = index + direction; step >= 0 && step < listBox.Items.Count; step += direction)
            {
                var row = listBox.Items[step] as ListBoxItem;
                if (!(row?.Tag is ModelOption)) continue;

                listBox.SelectedIndex = step;
                listBox.ScrollIntoView(listBox.Items[step]);
                return;
            }
        }

        /// <summary>
        /// Re-reads the provider's list and calls back on the UI thread when it lands. The picker is
        /// modal, so the dispatcher keeps pumping and the continuation runs while it is on screen;
        /// the callback is skipped when the window closed first.
        /// </summary>
        private void StartModelPickerRefresh(AiProvider provider, Action onCompleted)
        {
            try
            {
                _ = RefreshProviderModelsAsync(provider).ContinueWith(
                    delegate
                    {
                        ThreadHelper.ThrowIfNotOnUIThread();
                        onCompleted();
                    },
                    System.Threading.CancellationToken.None,
                    System.Threading.Tasks.TaskContinuationOptions.OnlyOnRanToCompletion,
                    System.Threading.Tasks.TaskScheduler.FromCurrentSynchronizationContext());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Model picker: refresh failed: {ex.Message}");
            }
        }

        #endregion
    }
}
