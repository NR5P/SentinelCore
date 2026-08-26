using System.IO;
using System.Windows;
using Microsoft.Win32;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class KnownFacesWindow : Window
{
    // Big enough that a phone photo goes through untouched, small enough that nobody uploads a
    // 40MB raw file by accident.
    private const long MaxPhotoBytes = 12 * 1024 * 1024;

    private readonly AppModel _model;

    public KnownFacesWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        Loaded += async (_, _) => await ReloadAsync();
    }

    public sealed record FaceRow(KnownFace Face)
    {
        public string Id => Face.Id;
        public string Label => Face.Label;

        public string Detail => Face.Enrollments.Count switch
        {
            0 => "No photos yet - this person can't be recognised until there is at least one",
            1 => "1 photo",
            var count => $"{count} photos",
        };

        public IReadOnlyList<EnrollmentRow> Enrollments =>
            Face.Enrollments.Select(enrollment => new EnrollmentRow(Face.Id, enrollment)).ToList();
    }

    public sealed record EnrollmentRow(string FaceId, KnownFaceEnrollment Enrollment)
    {
        public string PhotoUrl => Enrollment.PhotoUrl;
        public string EnrollmentId => Enrollment.Id;
    }

    private async Task ReloadAsync()
    {
        try
        {
            var faces = await _model.Client.KnownFacesAsync();
            FaceList.ItemsSource = faces.Select(face => new FaceRow(face)).ToList();
            Message(faces.Count == 0 ? "No known faces yet." : "");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnAddPersonClicked(object sender, RoutedEventArgs e)
    {
        var prompt = new TextPromptWindow("Add a person", "What is this person called?") { Owner = this };
        if (prompt.ShowDialog() != true) return;

        // A person is created together with their first photo: the endpoint takes both, and a
        // person with no photos cannot be recognised anyway.
        if (!TryPickPhoto(out var path, out var bytes)) return;

        try
        {
            await _model.Client.CreateKnownFaceAsync(prompt.Value, bytes, Path.GetFileName(path), MimeFor(path));
            await ReloadAsync();
            Message($"Added {prompt.Value}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnAddPhotoClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FaceRow row) return;
        if (!TryPickPhoto(out var path, out var bytes)) return;

        try
        {
            await _model.Client.AddKnownFaceEnrollmentAsync(row.Id, bytes, Path.GetFileName(path), MimeFor(path));
            await ReloadAsync();
            Message($"Added a photo for {row.Label}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnDeletePhotoClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not EnrollmentRow row) return;

        try
        {
            await _model.Client.DeleteKnownFaceEnrollmentAsync(row.FaceId, row.EnrollmentId);
            await ReloadAsync();
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private async void OnDeletePersonClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FaceRow row) return;

        var confirmed = MessageBox.Show(
            this,
            $"Delete \"{row.Label}\" and all their photos?\n\nFrom then on the AI describes them as a stranger.",
            "Delete person",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning) == MessageBoxResult.OK;
        if (!confirmed) return;

        try
        {
            await _model.Client.DeleteKnownFaceAsync(row.Id);
            await ReloadAsync();
            Message($"Deleted {row.Label}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private bool TryPickPhoto(out string path, out byte[] bytes)
    {
        path = "";
        bytes = Array.Empty<byte>();

        var dialog = new OpenFileDialog
        {
            Title = "Choose a photo",
            Filter = "Images|*.jpg;*.jpeg;*.png|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true) return false;

        var info = new FileInfo(dialog.FileName);
        if (info.Length > MaxPhotoBytes)
        {
            Message($"That photo is {Design.ByteSizeConverter.FormatBytes(info.Length)}. " +
                    "Pick one under 12 MB - a normal phone photo is well inside that.");
            return false;
        }

        try
        {
            bytes = File.ReadAllBytes(dialog.FileName);
            path = dialog.FileName;
            return true;
        }
        catch (Exception error)
        {
            Message($"Could not read that file: {error.Message}");
            return false;
        }
    }

    private static string MimeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".png" ? "image/png" : "image/jpeg";

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
