using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;

namespace ProPS1Studio.App.Views;

/// <summary>Simple Authenticode certificate picker: lists certificates from the
/// CurrentUser/LocalMachine Personal store and returns the chosen one's thumbprint.</summary>
public sealed partial class CertificatePickerWindow : Window
{
    public CertificatePickerWindow(string storeLocation)
    {
        InitializeComponent();

        var location = string.IsNullOrWhiteSpace(storeLocation) ? "CurrentUser" : storeLocation;
        Certificates.Clear();

        try
        {
            using var store = new X509Store(location + "\\My", StorePermissions.Read);
            store.Open(OpenFlags.ReadOnly);
            foreach (var cert in store.Certificates.OfType<X509Certificate2>().OrderByDescending(c => c.NotAfter))
            {
                Certificates.Add(new CertificateItem(
                    cert.Thumbprint,
                    cert.Subject,
                    cert.Issuer,
                    cert.NotAfter,
                    cert.NotAfter > DateTime.Now));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Could not read the certificate store '{location}\\My':\n{ex.Message}\n\n" +
                "Enter a thumbprint manually instead.",
                "Certificate store error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Title = $"Select a signing certificate ({location})";
        ListBox.DataContext = this;
    }

    public ObservableCollection<CertificateItem> Certificates { get; } = new();

    public string? SelectedThumbprint { get; private set; }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        var selected = ListBox.SelectedItem as CertificateItem;
        if (selected is null)
        {
            MessageBox.Show("Select a certificate first.", "No selection",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedThumbprint = selected.Thumbprint;
        DialogResult = true;
    }
}

public sealed record CertificateItem(
    string Thumbprint,
    string Subject,
    string Issuer,
    DateTime NotAfter,
    bool IsValid);
