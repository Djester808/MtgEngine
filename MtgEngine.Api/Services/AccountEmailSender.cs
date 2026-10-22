using System.Net;
using System.Net.Mail;

namespace MtgEngine.Api.Services;

public interface IAccountEmailSender
{
    Task SendAsync(string address, string subject, string body, CancellationToken ct);
}

public sealed class AccountEmailSender(IConfiguration config, IWebHostEnvironment environment, ILogger<AccountEmailSender> logger) : IAccountEmailSender
{
    public async Task SendAsync(string address, string subject, string body, CancellationToken ct)
    {
        var host = config["Email:Smtp:Host"];
        if (string.IsNullOrWhiteSpace(host))
        {
            if (!environment.IsDevelopment())
                throw new ConfigurationException("Account email delivery is not configured.");
            logger.LogInformation("Development email to {Address}: {Subject}\n{Body}", address, subject, body);
            return;
        }
        using var message = new MailMessage(config["Email:From"] ?? "no-reply@compleat.local", address, subject, body);
        using var smtp = new SmtpClient(host, config.GetValue("Email:Smtp:Port", 587))
        {
            EnableSsl = config.GetValue("Email:Smtp:UseStartTls", true),
            Credentials = new NetworkCredential(config["Email:Smtp:User"], config["Email:Smtp:Password"]),
        };
        await smtp.SendMailAsync(message, ct).ConfigureAwait(false);
    }
}
