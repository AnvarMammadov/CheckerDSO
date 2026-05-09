namespace CheckerDSO.Models
{
    public class ProxyEntry
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }

        public bool IsAuthenticated => !string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(Password);

        public override string ToString()
        {
            if (IsAuthenticated)
                return $"{Host}:{Port}:{Username}:{Password}";
            return $"{Host}:{Port}";
        }
    }
}
