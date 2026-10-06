using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Numerics;
using HaloMeister.App.Localization;
using HaloMeister.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace HaloMeister.App.Pages;

public sealed partial class CharacterModelPreviewWindow : UserControl
{
    private readonly CharacterModelLibraryService _models = CharacterModelLibraryService.Current;
    private readonly ObservableCollection<AnimationRow> _animations = [];
    private readonly List<AnimationRow> _allAnimations = [];
    private readonly Dictionary<string, string> _picks = new(StringComparer.OrdinalIgnoreCase);
    private readonly GpuModelViewport _gpu = new();
    private float[] _positions = [];
    private Vector3 _albedo = new(186 / 255f, 214 / 255f, 204 / 255f);
    private Vector3 _center;
    private Vector3 _pan;
    private float _radius = 1;
    private float _distance = 4;
    private float _yaw = 0.7f;
    private float _pitch = 0.35f;
    private string? _framedFor;
    private string? _id;
    private string? _variant;
    private int _generation;
    private bool _filling;
    private bool _closed;
    private bool _gpuAttached;
    private bool _dragging;
    private bool _panning;
    private bool _scrubbing;
    private bool _playing;
    private bool _loop = true;
    private bool _composed;
    private float _speed = 1f;
    private Point _lastPoint;
    private CharacterModelPose? _pose;
    private float _time;
    private long _stamp;
    private int _poseGeneration;

    public CharacterModelPreviewWindow()
    {
        InitializeComponent();
        AnimationBox.ItemsSource = _animations;
        AnimationBox.SelectionChanged += OnAnimationSelected;
        Unloaded += (_, _) =>
        {
            _closed = true;
            SetPlayback(false);
        };
        Viewport.Loaded += (_, _) =>
        {
            _closed = false;
            try
            {
                if (_gpuAttached)
                    _gpu.Restore();
                else
                {
                    _gpu.Attach(Viewport);
                    _gpuAttached = true;
                }
                Draw();
            }
            catch (Exception ex)
            {
                MessageText.Text = ex.Message;
                MessageText.Visibility = Visibility.Visible;
            }
        };
    }

    public void Suspend() => SetPlayback(false);

    public async Task ShowAsync(CharacterModelFile file)
    {
        bool switching = !string.Equals(_id, file.Id, StringComparison.OrdinalIgnoreCase);
        _id = file.Id;
        _variant = null;
        _picks.Clear();
        if (switching && file.Kind != "model")
            ClearOptions();
        await LoadAsync();
    }

    private async void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || VariantBox.SelectedItem is not string name)
            return;
        if (string.Equals(name, _variant, StringComparison.OrdinalIgnoreCase))
            return;
        _variant = name;
        _picks.Clear();
        await LoadAsync();
    }

    private async void OnPermutation(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
            return;
        int split = tag.IndexOf('\n');
        if (split <= 0)
            return;
        _picks[tag[..split]] = tag[(split + 1)..];
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (string.IsNullOrEmpty(_id))
            return;
        int generation = ++_generation;
        StopPlayback();
        BusyRing.IsActive = true;
        MessageText.Visibility = Visibility.Collapsed;
        try
        {
            CharacterModelPreview preview = await _models.InspectAsync(_id, _variant, _picks);
            if (generation != _generation || _closed)
                return;
            Apply(preview);
        }
        catch (Exception ex)
        {
            if (generation != _generation || _closed)
                return;
            MessageText.Text = ex.Message;
            MessageText.Visibility = Visibility.Visible;
            _positions = [];
            _gpu.SetMesh([], [], [], _albedo, [], []);
            Draw();
        }
        finally
        {
            if (generation == _generation && !_closed)
                BusyRing.IsActive = false;
        }
    }

    private void Apply(CharacterModelPreview preview)
    {
        StatsText.Text = L.Format("character_model.stats", preview.Vertices, preview.Triangles);
        _albedo = preview.Kind == "model"
            ? new Vector3(186 / 255f, 214 / 255f, 204 / 255f)
            : new Vector3(214 / 255f, 140 / 255f, 168 / 255f);
        SidePanel.Visibility = Visibility.Visible;
        SideColumn.Width = new GridLength(360);
        PlayerBar.Visibility = Visibility.Visible;
        _positions = preview.Positions;
        _gpu.SetMesh(preview.Positions, preview.Normals, preview.Indices, _albedo, preview.Joints, preview.Weights);
        if (!string.Equals(_framedFor, preview.Id, StringComparison.Ordinal))
        {
            _framedFor = preview.Id;
            FitCamera();
        }
        if (preview.Indices.Length < 3)
        {
            MessageText.Text = L.Get("character_model.no_mesh");
            MessageText.Visibility = Visibility.Visible;
        }

        _filling = true;
        VariantBox.Items.Clear();
        foreach (CharacterModelVariant variant in preview.Variants)
            VariantBox.Items.Add(variant.Name);
        string selectedVariant = string.IsNullOrEmpty(preview.Variant)
            ? preview.Variants.FirstOrDefault()?.Name ?? ""
            : preview.Variant;
        VariantBox.SelectedItem = VariantBox.Items.Cast<string>().FirstOrDefault(name =>
            name.Equals(selectedVariant, StringComparison.OrdinalIgnoreCase));
        _variant = VariantBox.SelectedItem as string ?? selectedVariant;
        _filling = false;

        RegionHost.Children.Clear();
        Style? accent = Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style)
            ? style as Style
            : null;
        foreach (CharacterModelRegion region in preview.Regions)
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock { Text = region.Name, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            var row = new OptionWrap { Spacing = 4 };
            foreach (string perm in region.Permutations)
            {
                var button = new Button
                {
                    Content = new TextBlock { Text = perm, TextWrapping = TextWrapping.Wrap },
                    Tag = region.Name + "\n" + perm,
                    MinWidth = 64,
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                if (perm.Equals(region.Selected, StringComparison.OrdinalIgnoreCase) && accent is not null)
                    button.Style = accent;
                button.Click += OnPermutation;
                row.Children.Add(button);
            }
            block.Children.Add(row);
            RegionHost.Children.Add(block);
        }

        _allAnimations.Clear();
        foreach (CharacterModelAnimation animation in preview.Animations)
        {
            _allAnimations.Add(new AnimationRow(
                animation,
                L.Format("character_model.animation_row", animation.Name, animation.Kind, animation.Frames)));
        }
        ApplyAnimationFilter();
        Draw();
    }

    private void ApplyAnimationFilter()
    {
        bool filling = _filling;
        _filling = true;
        _animations.Clear();
        foreach (AnimationRow row in _allAnimations)
            _animations.Add(row);
        bool empty = _allAnimations.Count == 0;
        if (empty)
            AnimationBox.SelectedItem = null;
        AnimationBox.IsEnabled = !empty;
        AnimationBox.PlaceholderText = L.Get(empty ? "character_model.no_animations" : "character_model.animations");
        PlayButton.IsEnabled = !empty;
        PauseButton.IsEnabled = !empty;
        StopButton.IsEnabled = !empty;
        LoopButton.IsEnabled = !empty;
        SpeedBox.IsEnabled = !empty;
        FrameSlider.IsEnabled = !empty;
        if (empty)
            FrameLabel.Text = "";
        _filling = filling;
    }

    private void ClearOptions()
    {
        StopPlayback();
        _filling = true;
        VariantBox.Items.Clear();
        VariantBox.SelectedItem = null;
        _variant = null;
        RegionHost.Children.Clear();
        _allAnimations.Clear();
        _filling = false;
        ApplyAnimationFilter();
    }

    private void FitCamera()
    {
        if (_positions.Length < 3)
        {
            _center = Vector3.Zero;
            _radius = 1;
            _distance = 4;
            _pan = Vector3.Zero;
            return;
        }
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i + 2 < _positions.Length; i += 3)
        {
            var point = new Vector3(_positions[i], _positions[i + 1], _positions[i + 2]);
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
        _center = (min + max) * 0.5f;
        _radius = MathF.Max(0.001f, (max - min).Length() * 0.5f);
        _distance = _radius * 2.6f;
        _pan = Vector3.Zero;
        _yaw = 0.8f;
        _pitch = 0.28f;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _panning = e.GetCurrentPoint(Viewport).Properties.IsRightButtonPressed;
        _lastPoint = e.GetCurrentPoint(Viewport).Position;
        Viewport.CapturePointer(e.Pointer);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        Viewport.ReleasePointerCapture(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        Point point = e.GetCurrentPoint(Viewport).Position;
        float dx = (float)(point.X - _lastPoint.X);
        float dy = (float)(point.Y - _lastPoint.Y);
        _lastPoint = point;
        if (_panning)
        {
            Basis(out Vector3 right, out Vector3 up);
            float scale = _distance / MathF.Max(1f, (float)Viewport.ActualHeight);
            _pan += (-right * dx + up * dy) * scale;
        }
        else
        {
            _yaw += dx * 0.01f;
            _pitch = Math.Clamp(_pitch + dy * 0.01f, -1.2f, 1.2f);
        }
        Draw();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(Viewport).Properties.MouseWheelDelta;
        _distance = Math.Clamp(_distance * (delta > 0 ? 0.9f : 1.1f), _radius * 0.2f, _radius * 20f);
        Draw();
    }

    private void Basis(out Vector3 right, out Vector3 up)
    {
        float cp = MathF.Cos(_pitch);
        float sp = MathF.Sin(_pitch);
        float cy = MathF.Cos(_yaw);
        float sy = MathF.Sin(_yaw);
        var forward = Vector3.Normalize(new Vector3(-cp * sy, -cp * cy, -sp));
        right = Vector3.Cross(forward, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f)
            right = Vector3.UnitX;
        right = Vector3.Normalize(right);
        up = Vector3.Cross(right, forward);
    }

    private void OnAnimationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _closed)
            return;
        if (AnimationBox.SelectedItem is not AnimationRow row || string.IsNullOrEmpty(_id))
            return;
        _ = PlayAnimationAsync(row.Animation);
    }

    private async Task PlayAnimationAsync(CharacterModelAnimation animation)
    {
        if (string.IsNullOrEmpty(_id))
            return;
        int generation = ++_poseGeneration;
        BusyRing.IsActive = true;
        try
        {
            CharacterModelPose pose = await _models.PoseAsync(_id, animation.Index);
            if (generation != _poseGeneration || _closed)
                return;
            MessageText.Visibility = Visibility.Collapsed;
            if (generation != _poseGeneration || _closed || pose.Frames == 0 || pose.Bones == 0)
                return;
            _pose = pose;
            _time = 0;
            _gpu.SetClip(pose.Matrices, pose.Bones, pose.Frames);
            _scrubbing = true;
            FrameSlider.Maximum = Math.Max(1, pose.Frames - 1);
            FrameSlider.Value = 0;
            _scrubbing = false;
            SamplePose(0);
            SetPlayback(true);
            Draw();
        }
        catch (Exception ex)
        {
            if (generation != _poseGeneration || _closed)
                return;
            MessageText.Text = ex.Message;
            MessageText.Visibility = Visibility.Visible;
        }
        finally
        {
            if (generation == _poseGeneration && !_closed)
                BusyRing.IsActive = false;
        }
    }

    private void OnPlay(object sender, RoutedEventArgs e)
    {
        if (_pose is null || _pose.Frames == 0)
        {
            if (AnimationBox.SelectedItem is AnimationRow row)
                _ = PlayAnimationAsync(row.Animation);
            return;
        }
        if (!_loop && _time * 30f >= _pose.Frames - 1)
        {
            _time = 0;
            SamplePose(0);
        }
        SetPlayback(true);
        Draw();
    }

    private void OnPause(object sender, RoutedEventArgs e) => SetPlayback(false);

    private void OnStop(object sender, RoutedEventArgs e)
    {
        if (_pose is null)
            return;
        SetPlayback(false);
        _time = 0;
        _scrubbing = true;
        FrameSlider.Value = 0;
        _scrubbing = false;
        SamplePose(0);
        Draw();
    }

    private void OnLoop(object sender, RoutedEventArgs e) =>
        _loop = LoopButton.IsChecked == true;

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedBox.SelectedItem is ComboBoxItem { Tag: string text }
            && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float speed)
            && speed > 0)
            _speed = speed;
    }

    private void OnFrameChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_scrubbing || _pose is null || _filling)
            return;
        SetPlayback(false);
        _time = (float)FrameSlider.Value / 30f;
        SamplePose((float)FrameSlider.Value);
        Draw();
    }

    private void OnComposed(object? sender, object e)
    {
        if (!_playing || _pose is null || _closed)
            return;
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _stamp) / (double)Stopwatch.Frequency;
        _stamp = now;
        AdvancePlayback(Math.Clamp(dt, 0, 0.05));
    }

    private void SetPlayback(bool playing)
    {
        _playing = playing;
        if (playing)
        {
            _stamp = Stopwatch.GetTimestamp();
            if (_composed)
                return;
            _composed = true;
            CompositionTarget.Rendering += OnComposed;
            return;
        }
        if (!_composed)
            return;
        CompositionTarget.Rendering -= OnComposed;
        _composed = false;
    }

    private void AdvancePlayback(double dt)
    {
        if (!_playing || _pose is null || _pose.Frames == 0 || _closed)
            return;
        float span = Math.Max(1, _pose.Frames);
        _time += (float)dt * _speed;
        float frame = _time * 30f;
        if (frame >= span)
        {
            if (_loop)
            {
                frame %= span;
                _time = frame / 30f;
            }
            else
            {
                frame = span - 1;
                _time = frame / 30f;
                SetPlayback(false);
            }
        }
        _scrubbing = true;
        FrameSlider.Value = frame;
        _scrubbing = false;
        SamplePose(frame);
        Draw();
    }

    private void SamplePose(float frame)
    {
        if (_pose is null || _pose.Bones == 0 || _pose.Frames == 0)
            return;
        _gpu.SetAnimFrame(frame);
        int shown = Math.Clamp((int)MathF.Floor(frame), 0, _pose.Frames - 1);
        FrameLabel.Text = L.Format("character_model.progress", shown + 1, _pose.Frames);
    }

    private void StopPlayback()
    {
        _poseGeneration++;
        SetPlayback(false);
        _pose = null;
        _time = 0;
        _gpu.SetClip([], 0, 0);
        _gpu.SetAnimFrame(0);
        _scrubbing = true;
        FrameSlider.Value = 0;
        FrameSlider.Maximum = 1;
        _scrubbing = false;
        FrameLabel.Text = "";
    }

    private void Draw()
    {
        if (_closed)
            return;
        _gpu.Render(new ViewFrame(_center, _pan, _yaw, _pitch, _distance, _radius, _albedo));
    }
}

public sealed class OptionWrap : Panel
{
    public double Spacing { get; set; } = 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        double limit = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width);
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        double used = 0;
        foreach (UIElement child in Children)
        {
            child.Measure(new Size(limit, double.PositiveInfinity));
            double width = child.DesiredSize.Width;
            double height = child.DesiredSize.Height;
            if (x > 0 && x + width > limit)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            x += width + Spacing;
            rowHeight = Math.Max(rowHeight, height);
            used = Math.Max(used, x - Spacing);
        }
        double panelWidth = double.IsInfinity(limit) ? used : limit;
        double panelHeight = y + rowHeight;
        return new Size(panelWidth, panelHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        foreach (UIElement child in Children)
        {
            double width = child.DesiredSize.Width;
            double height = child.DesiredSize.Height;
            if (x > 0 && x + width > finalSize.Width)
            {
                y += rowHeight + Spacing;
                x = 0;
                rowHeight = 0;
            }
            child.Arrange(new Rect(x, y, width, height));
            x += width + Spacing;
            rowHeight = Math.Max(rowHeight, height);
        }
        return finalSize;
    }
}

public sealed class AnimationRow
{
    public AnimationRow(CharacterModelAnimation animation, string label)
    {
        Animation = animation;
        Label = label;
    }

    public CharacterModelAnimation Animation { get; }
    public string Label { get; }
}
