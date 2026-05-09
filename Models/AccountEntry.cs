using System.ComponentModel;

namespace CheckerDSO.Models
{
    public enum AccountStatus
    {
        Pending,
        Checking,
        Verified,
        Unverified,
        WrongPass,
        Error,
        Captcha
    }

    public class AccountEntry : INotifyPropertyChanged
    {
        private AccountStatus _status;
        private string _notes;

        public string Email { get; set; }
        public string Password { get; set; }

        public AccountStatus Status
        {
            get => _status;
            set
            {
                if (_status != value)
                {
                    _status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        public string Notes
        {
            get => _notes;
            set
            {
                if (_notes != value)
                {
                    _notes = value;
                    OnPropertyChanged(nameof(Notes));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
