using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using CheckerDSO.Models;
using System.Linq;

namespace CheckerDSO.Services
{
    public class CheckerService
    {
        private readonly List<ProxyEntry> _proxies;
        private int _proxyIndex = 0;
        private readonly object _proxyLock = new object();

        // Varsayılan DSO Login Endpoint - test edilmeli/değiştirilebilir olmalı
        private const string LoginUrl = "https://www.drakensang.com/api/user/login"; 
        private const string ManageUrl = "https://www.drakensang.com/account/manage";

        public CheckerService(List<ProxyEntry> proxies)
        {
            _proxies = proxies ?? new List<ProxyEntry>();
        }

        private ProxyEntry GetNextProxy()
        {
            if (_proxies == null || _proxies.Count == 0) return null;

            lock (_proxyLock)
            {
                var proxy = _proxies[_proxyIndex];
                _proxyIndex = (_proxyIndex + 1) % _proxies.Count;
                return proxy;
            }
        }

        public async Task CheckAccountAsync(AccountEntry account, CancellationToken token)
        {
            if (account.Status != AccountStatus.Pending) return;

            account.Status = AccountStatus.Checking;
            account.Notes = "Connecting...";

            var proxy = GetNextProxy();
            
            try
            {
                using var client = HttpService.CreateClient(proxy);

                // 1. POST Login
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("email", account.Email),
                    new KeyValuePair<string, string>("password", account.Password)
                });

                account.Notes = "Logging in...";
                var loginResponse = await client.PostAsync(LoginUrl, content, token);
                var loginContent = await loginResponse.Content.ReadAsStringAsync(token);

                if (loginContent.Contains("wrong password") || loginContent.Contains("invalid credentials"))
                {
                    account.Status = AccountStatus.WrongPass;
                    account.Notes = "Wrong Password";
                    return;
                }
                
                if (loginContent.Contains("captcha") || loginContent.Contains("g-recaptcha"))
                {
                    account.Status = AccountStatus.Captcha;
                    account.Notes = "Captcha detected";
                    return;
                }

                // 2. GET Manage Account
                account.Notes = "Checking email status...";
                var manageResponse = await client.GetAsync(ManageUrl, token);
                var manageHtml = await manageResponse.Content.ReadAsStringAsync(token);

                var doc = new HtmlDocument();
                doc.LoadHtml(manageHtml);

                // TODO: Gerçek DSO HTML yapısına göre bu kelimeler güncellenmeli
                string htmlLower = manageHtml.ToLower();
                if (htmlLower.Contains("confirm your email") || 
                    htmlLower.Contains("verify-email") || 
                    htmlLower.Contains("resend-verification"))
                {
                    account.Status = AccountStatus.Unverified;
                    account.Notes = "Email Unverified";
                }
                else
                {
                    // Eğer confirm butonları yoksa verified varsayıyoruz
                    account.Status = AccountStatus.Verified;
                    account.Notes = "Email Verified";
                }
            }
            catch (TaskCanceledException)
            {
                account.Status = AccountStatus.Pending;
                account.Notes = "Cancelled";
            }
            catch (HttpRequestException ex)
            {
                account.Status = AccountStatus.Error;
                account.Notes = $"Http Error: {ex.Message}";
            }
            catch (Exception ex)
            {
                account.Status = AccountStatus.Error;
                account.Notes = $"Error: {ex.Message}";
            }
            finally
            {
                // Proxy timeout vb durumlar için delay
                await Task.Delay(new Random().Next(500, 2000), CancellationToken.None);
            }
        }

        public async Task StartCheckingAsync(IEnumerable<AccountEntry> accounts, int maxThreads, IProgress<int> progress, CancellationToken token)
        {
            var semaphore = new SemaphoreSlim(maxThreads);
            var tasks = new List<Task>();

            foreach (var account in accounts.Where(a => a.Status == AccountStatus.Pending))
            {
                token.ThrowIfCancellationRequested();

                await semaphore.WaitAsync(token);

                var t = Task.Run(async () =>
                {
                    try
                    {
                        await CheckAccountAsync(account, token);
                    }
                    finally
                    {
                        semaphore.Release();
                        progress?.Report(1);
                    }
                }, token);

                tasks.Add(t);
            }

            await Task.WhenAll(tasks);
        }
    }
}
