using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotView.Services.Trading;

/// <summary>Produces display errors without signed URLs, request bodies or authentication material.</summary>
public static class TradingErrorText
{
    /// <summary>Extracts only the exchange's message field, or a safe category for transport errors.</summary>
    public static string FromException(Exception exception)
    {
        var error = exception.GetBaseException();
        if (error is TradingServiceException) return error.Message;
        if (error is OperationCanceledException or TimeoutException or ccxt.NetworkError)
            return "The response was not received. The operation outcome is unknown; check exchange orders before trying again.";
        if (error is ccxt.AuthenticationError) return "Authentication failed. Check API keys, network and permissions.";
        if (error is ccxt.ExchangeError)
        {
            var start = error.Message.IndexOf('{');
            if (start >= 0)
            {
                try
                {
                    using var json = JsonDocument.Parse(error.Message[start..]);
                    foreach (var field in new[] { "msg", "retMsg", "message" })
                    {
                        if (json.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
                        {
                            var message = value.GetString() ?? "";
                            if (message.Length <= 300 && !Regex.IsMatch(message, "https?://|api.?key|secret|signature|token", RegexOptions.IgnoreCase))
                                return message;
                        }
                    }
                }
                catch (JsonException) { }
            }
            return "The exchange rejected the request. Check order parameters and account permissions.";
        }
        if (error is NotSupportedException) return "This operation is not supported by the selected connection.";
        return "The request could not be completed. Check the connection and order parameters.";
    }
}
