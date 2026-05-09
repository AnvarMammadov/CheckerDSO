using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Win32;
using CheckerDSO.Models;
using CheckerDSO.Services;
using CheckerDSO.Helpers;

namespace CheckerDSO.ViewModels
{
    public class MainViewModel : BaseViewModel
    {
        private ObservableCollection<AccountRowViewModel> _accounts;
        private List<ProxyEntry> _proxies;
        
        private int _totalCount;
        private int _verifiedCount;
        private int _unverifiedCount;
        private int _wrongPassCount;
        private int _errorCount;
        private int _captchaCount;
        private int _threadCount = 5;
        private string _twoCaptchaApiKey = "";
        
        private bool _isRunning;
        private string _statusMessage = "Ready";

        private CancellationTokenSource _cts;

        public MainViewModel()
        {
            Accounts = new ObservableCollection<AccountRowViewModel>();
            _proxies = new List<ProxyEntry>();

            LoadLogsCommand = new RelayCommand(ExecuteLoadLogs, CanExecuteLoadLogs);
            LoadProxiesCommand = new RelayCommand(ExecuteLoadProxies, CanExecuteLoadProxies);
            StartCommand = new RelayCommand(ExecuteStart, CanExecuteStart);
            StopCommand = new RelayCommand(ExecuteStop, CanExecuteStop);
            ExportUnverifiedCommand = new RelayCommand(ExecuteExportUnverified, CanExecuteExportUnverified);
        }

        #region Properties

        public ObservableCollection<AccountRowViewModel> Accounts
        {
            get => _accounts;
            set => SetProperty(ref _accounts, value);
        }

        public int ThreadCount
        {
            get => _threadCount;
            set => SetProperty(ref _threadCount, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (SetProperty(ref _isRunning, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public int TotalCount { get => _totalCount; set => SetProperty(ref _totalCount, value); }
        public int VerifiedCount { get => _verifiedCount; set => SetProperty(ref _verifiedCount, value); }
        public int UnverifiedCount { get => _unverifiedCount; set => SetProperty(ref _unverifiedCount, value); }
        public int WrongPassCount { get => _wrongPassCount; set => SetProperty(ref _wrongPassCount, value); }
        public int ErrorCount { get => _errorCount; set => SetProperty(ref _errorCount, value); }
        public int CaptchaCount { get => _captchaCount; set => SetProperty(ref _captchaCount, value); }

        public string TwoCaptchaApiKey
        {
            get => _twoCaptchaApiKey;
            set => SetProperty(ref _twoCaptchaApiKey, value);
        }

        #endregion

        #region Commands

        public ICommand LoadLogsCommand { get; }
        public ICommand LoadProxiesCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand ExportUnverifiedCommand { get; }

        private bool CanExecuteLoadLogs(object obj) => !IsRunning;
        private void ExecuteLoadLogs(object obj)
        {
            var dialog = new OpenFileDialog { Filter = "Text Files|*.txt", Title = "Select Combos (user:pass)" };
            if (dialog.ShowDialog() == true)
            {
                var lines = File.ReadAllLines(dialog.FileName);
                Accounts.Clear();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    
                    string user = null;
                    string pass = null;

                    // Support format: url|user|pass
                    if (line.Contains("|"))
                    {
                        var parts = line.Split('|');
                        if (parts.Length >= 4)
                        {
                            user = parts[parts.Length - 3].Trim();
                            pass = parts[parts.Length - 2].Trim();
                        }
                    }
                    // Support format: id;user;pass
                    else if (line.Contains(";"))
                    {
                        var parts = line.Split(';');
                        if (parts.Length >= 3)
                        {
                            user = parts[1].Trim();
                            pass = parts[2].Trim();
                        }
                    }
                    // Support standard format: user:pass (and handle extra garbage before user)
                    else if (line.Contains(":"))
                    {
                        var parts = line.Split(':');
                        if (parts.Length >= 2)
                        {
                            user = parts[parts.Length - 2].Split(' ').Last().Trim();
                            pass = parts[parts.Length - 1].Trim();
                        }
                    }

                    if (!string.IsNullOrEmpty(user) && !string.IsNullOrEmpty(pass))
                    {
                        var entry = new AccountEntry { Email = user, Password = pass, Status = AccountStatus.Pending };
                        entry.PropertyChanged += Entry_PropertyChanged;
                        Accounts.Add(new AccountRowViewModel(entry));
                    }
                }
                UpdateStats();
                StatusMessage = $"Loaded {Accounts.Count} accounts.";
            }
        }

        private bool CanExecuteLoadProxies(object obj) => !IsRunning;
        private void ExecuteLoadProxies(object obj)
        {
            var dialog = new OpenFileDialog { Filter = "Text Files|*.txt", Title = "Select Proxies" };
            if (dialog.ShowDialog() == true)
            {
                var lines = File.ReadAllLines(dialog.FileName);
                _proxies.Clear();
                foreach (var line in lines)
                {
                    var parts = line.Split(':');
                    if (parts.Length == 2)
                    {
                        _proxies.Add(new ProxyEntry { Host = parts[0].Trim(), Port = int.Parse(parts[1].Trim()) });
                    }
                    else if (parts.Length == 4)
                    {
                        _proxies.Add(new ProxyEntry { Host = parts[0].Trim(), Port = int.Parse(parts[1].Trim()), Username = parts[2].Trim(), Password = parts[3].Trim() });
                    }
                }
                StatusMessage = $"Loaded {_proxies.Count} proxies.";
            }
        }

        private bool CanExecuteStart(object obj) => !IsRunning && Accounts.Count > 0;
        private async void ExecuteStart(object obj)
        {
            IsRunning = true;
            StatusMessage = "Checking...";
            _cts = new CancellationTokenSource();

            var checker = new CheckerService(_proxies) { TwoCaptchaApiKey = TwoCaptchaApiKey };
            var pendingAccounts = Accounts.Select(a => a.Account).ToList();

            var progress = new Progress<int>(_ => UpdateStats());

            try
            {
                await checker.StartCheckingAsync(pendingAccounts, ThreadCount, progress, _cts.Token);
                StatusMessage = "Finished!";
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Stopped.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsRunning = false;
            }
        }

        private bool CanExecuteStop(object obj) => IsRunning;
        private void ExecuteStop(object obj)
        {
            _cts?.Cancel();
            StatusMessage = "Stopping...";
        }

        private bool CanExecuteExportUnverified(object obj) => !IsRunning && UnverifiedCount > 0;
        private void ExecuteExportUnverified(object obj)
        {
            var dialog = new SaveFileDialog { Filter = "Text Files|*.txt", Title = "Save Unverified Accounts" };
            if (dialog.ShowDialog() == true)
            {
                var unverified = Accounts.Where(a => a.Status == AccountStatus.Unverified)
                                         .Select(a => $"{a.Email}:{a.Password}");
                File.WriteAllLines(dialog.FileName, unverified);
                StatusMessage = $"Exported {unverified.Count()} accounts.";
            }
        }

        #endregion

        private void Entry_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AccountEntry.Status))
            {
                App.Current.Dispatcher.Invoke(() => UpdateStats());
            }
        }

        private void UpdateStats()
        {
            TotalCount = Accounts.Count;
            VerifiedCount = Accounts.Count(a => a.Status == AccountStatus.Verified);
            UnverifiedCount = Accounts.Count(a => a.Status == AccountStatus.Unverified);
            WrongPassCount = Accounts.Count(a => a.Status == AccountStatus.WrongPass);
            ErrorCount = Accounts.Count(a => a.Status == AccountStatus.Error);
            CaptchaCount = Accounts.Count(a => a.Status == AccountStatus.Captcha);
        }
    }
}
