using System.Net.Sockets;
using System.Text.Json;

namespace DeskBridge.Core.Control;

/// <summary>The coordinator side: connects to an agent, authenticates and sends one request.</summary>
public static class ControlClient
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <param name="expectedMachine">Id the agent must announce, to catch a wrong IP in the config.</param>
    public static async Task<ControlMessage> SendAsync(
        string host,
        int port,
        string localMachine,
        string expectedMachine,
        byte[] key,
        ControlMessage request,
        CancellationToken ct = default)
    {
        using var tcp = new TcpClient();
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await tcp.ConnectAsync(host, port, connectCts.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ControlException($"No se pudo conectar con {host}:{port} ({ex.Message}). ¿Está el agente arrancado y el puerto abierto en el firewall?");
        }

        try
        {
            var channel = new LineChannel(tcp.GetStream());

            if (await channel.ReceiveAsync(ct) is not ChallengeMessage challenge)
                throw new ControlException($"{host}:{port} no parece un agente DeskBridge.");
            if (challenge.Version != ControlProtocol.Version)
                throw new ControlException($"Versión de protocolo distinta en {host} ({challenge.Version}, aquí {ControlProtocol.Version}). Actualiza los dos equipos.");
            if (challenge.Machine != expectedMachine)
                throw new ControlException($"En {host} responde '{challenge.Machine}', no '{expectedMachine}'. Revisa la dirección en la configuración.");

            var proof = ControlAuth.Proof(key, challenge.Nonce, localMachine, challenge.Machine);
            await channel.SendAsync(new AuthMessage(localMachine, proof), ct);

            switch (await channel.ReceiveAsync(ct))
            {
                case WelcomeMessage:
                    break;
                case ErrorMessage error:
                    throw new ControlException(error.Message);
                default:
                    throw new ControlException($"Respuesta inesperada de {host} durante la autenticación.");
            }

            await channel.SendAsync(request, ct);
            return await channel.ReceiveAsync(ct)
                ?? throw new ControlException($"{host} cerró la conexión sin responder.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or InvalidDataException)
        {
            throw new ControlException($"Error de comunicación con {host}: {ex.Message}");
        }
    }
}
