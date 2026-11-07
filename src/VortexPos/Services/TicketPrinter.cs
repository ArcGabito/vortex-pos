using System.Net.Sockets;
using VortexPos.Core.Printing;

using static VortexPos.Services.Txt;

namespace VortexPos.Services;

/// <summary>
/// Impresora de tickets de esta caja: una térmica ESC/POS en la red (puerto 9100, el estándar "RAW" de Epson, Bixolon,
/// Xprinter y compatibles). Cada caja tiene la suya, por eso se guarda en el equipo y no en la configuración de la tienda.
/// El cajón de dinero va conectado a la impresora y se abre con un pulso desde ella.
/// </summary>
public sealed class TicketPrinter
{
    private const string HostKey = "vortex.printer.host";
    private const string PortKey = "vortex.printer.port";
    public const int DefaultPort = 9100;

    public string? Host => Preferences.Default.Get<string?>(HostKey, null) is { Length: > 0 } host ? host : null;
    public int Port => Preferences.Default.Get(PortKey, DefaultPort);
    public bool IsConfigured => Host is not null;

    public void Configure(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            Preferences.Default.Remove(HostKey);
            Preferences.Default.Remove(PortKey);
            return;
        }
        Preferences.Default.Set(HostKey, host.Trim());
        Preferences.Default.Set(PortKey, port is > 0 and < 65536 ? port : DefaultPort);
    }

    /// <summary>Imprime el ticket. Devuelve el mensaje de error para el usuario, o null si salió bien.</summary>
    public Task<string?> PrintAsync(TicketDocument ticket) => SendAsync(EscPos.Encode(ticket));

    public Task<string?> OpenDrawerAsync() => SendAsync(EscPos.DrawerPulse);

    private async Task<string?> SendAsync(byte[] bytes)
    {
        if (Host is not { } host)
            return T("printer.no_hay_impresora");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var client = new TcpClient();
            await client.ConnectAsync(host, Port, timeout.Token);
            await using var stream = client.GetStream();
            await stream.WriteAsync(bytes, timeout.Token);
            await stream.FlushAsync(timeout.Token);
            return null;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            return T("printer.no_responde", host, Port);
        }
    }
}
