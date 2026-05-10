using System;
using System.Configuration;
using System.Net;
using System.Net.Http;
using CheckerDSO.Models;

namespace CheckerDSO.Services
{
    public class HttpService
    {
        public static HttpClient CreateClient(ProxyEntry proxy, int timeoutSeconds = 30)
        {
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                // Proxy'lerle SSL sertifikat hatalarını yoksay
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            if (proxy != null)
            {
                var webProxy = new WebProxy(proxy.Host, proxy.Port);
                if (proxy.IsAuthenticated)
                {
                    webProxy.Credentials = new NetworkCredential(proxy.Username, proxy.Password);
                }
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            // ─── Chrome 124 Browser Fingerprint Headers ───
            // Sıralama önemli! Real Chrome'un gönderdiği sırayla gönderiyoruz.
            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

            client.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7");

            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");

            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");

            // Chrome UA-CH (User-Agent Client Hints) — bot tespitinde kritik
            client.DefaultRequestHeaders.Add("sec-ch-ua",
                "\"Chromium\";v=\"124\", \"Google Chrome\";v=\"124\", \"Not-A.Brand\";v=\"99\"");
            client.DefaultRequestHeaders.Add("sec-ch-ua-mobile", "?0");
            client.DefaultRequestHeaders.Add("sec-ch-ua-platform", "\"Windows\"");

            // Removed sec-fetch-* headers from global config to avoid polluting cross-origin redirects
            client.DefaultRequestHeaders.Add("upgrade-insecure-requests", "1");

            return client;
        }

        /// <summary>
        /// BrightData Web Unlocker proxy ile HttpClient oluşturur.
        /// Web Unlocker captcha'yı, bot korumasını ve IP rotasyonunu otomatik halleder.
        /// 
        /// Dashboard: https://brightdata.com → Web Unlocker zone oluştur
        /// Username: brd-customer-XXXX-zone-YYYY
        /// Password: zone şifresi
        /// </summary>
        public static HttpClient CreateClientWithBrightData(string username, string password, int timeoutSeconds = 60)
        {
            var webProxy = new WebProxy("brd.superproxy.io", 33335)  // Web Unlocker port
            {
                Credentials = new NetworkCredential(username, password)
            };

            var handler = new HttpClientHandler
            {
                Proxy = webProxy,
                UseProxy = true,
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer(),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                // BrightData kendi SSL sertifikasını kullanır
          
            };

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

            client.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
            client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
            client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
            client.DefaultRequestHeaders.Add("upgrade-insecure-requests", "1");

            return client;
        }
    }
}
