using System.Collections.ObjectModel;
using System.Windows;
using SentinelCore.Client;
using SentinelCore.Client.Models;

namespace SentinelCore.App.Views;

public partial class KnownVehiclesWindow : Window
{
    private readonly AppModel _model;
    private readonly ObservableCollection<KnownVehicle> _vehicles = new();

    public KnownVehiclesWindow(AppModel model)
    {
        _model = model;
        InitializeComponent();
        VehicleList.ItemsSource = _vehicles;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        try
        {
            _vehicles.Clear();
            foreach (var vehicle in await _model.Client.KnownVehiclesAsync()) _vehicles.Add(vehicle);
            Message("");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
    }

    private void OnAddClicked(object sender, RoutedEventArgs e)
    {
        // Plates are compared case-insensitively by the matcher, and the spacing people type varies,
        // so they are stored upper-case and trimmed rather than exactly as typed.
        var plate = PlateBox.Text.Trim().ToUpperInvariant();
        var label = LabelBox.Text.Trim();

        if (plate.Length == 0)
        {
            Message("Enter the number plate.");
            return;
        }
        if (_vehicles.Any(vehicle => vehicle.Plate.Equals(plate, StringComparison.OrdinalIgnoreCase)))
        {
            Message($"{plate} is already on the list.");
            return;
        }

        _vehicles.Add(new KnownVehicle { Plate = plate, Label = label });
        PlateBox.Clear();
        LabelBox.Clear();
        Message("Not saved yet - press Save.");
    }

    private void OnRemoveClicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KnownVehicle vehicle) return;
        _vehicles.Remove(vehicle);
        Message("Not saved yet - press Save.");
    }

    private async void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            // The whole list every time, which is what the endpoint takes - it replaces the section
            // rather than patching it, so a partial send would delete the rest.
            await _model.Client.SaveKnownVehiclesAsync(_vehicles.ToList());
            Message($"Saved {_vehicles.Count} vehicle{(_vehicles.Count == 1 ? "" : "s")}.");
        }
        catch (Exception error)
        {
            Message(AppModel.UserFacingMessage(error));
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Message(string message) => MessageText.Text = message;

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();
}
