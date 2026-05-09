using CheckerDSO.Models;

namespace CheckerDSO.ViewModels
{
    public class AccountRowViewModel : BaseViewModel
    {
        private AccountEntry _account;

        public AccountRowViewModel(AccountEntry account)
        {
            _account = account;
            _account.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
        }

        public AccountEntry Account => _account;

        public string Email => _account.Email;
        public string Password => _account.Password;

        public AccountStatus Status
        {
            get => _account.Status;
            set => _account.Status = value;
        }

        public string Notes
        {
            get => _account.Notes;
            set => _account.Notes = value;
        }
    }
}
