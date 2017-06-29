﻿using System;
using Bit.App.Abstractions;
using Bit.App.Controls;
using Bit.App.Resources;
using Xamarin.Forms;
using XLabs.Ioc;
using Acr.UserDialogs;
using System.Threading.Tasks;
using PushNotification.Plugin.Abstractions;
using Bit.App.Models;
using Bit.App.Utilities;
using Bit.App.Enums;
using System.Collections.Generic;
using System.Net;
using FFImageLoading.Forms;

namespace Bit.App.Pages
{
    public class LoginTwoFactorPage : ExtendedContentPage
    {
        private DateTime? _lastAction;
        private IAuthService _authService;
        private IUserDialogs _userDialogs;
        private ISyncService _syncService;
        private IDeviceInfoService _deviceInfoService;
        private IGoogleAnalyticsService _googleAnalyticsService;
        private ITwoFactorApiRepository _twoFactorApiRepository;
        private IPushNotification _pushNotification;
        private readonly string _email;
        private readonly string _masterPasswordHash;
        private readonly SymmetricCryptoKey _key;
        private readonly Dictionary<TwoFactorProviderType, Dictionary<string, object>> _providers;
        private TwoFactorProviderType? _providerType;
        private readonly FullLoginResult _result;

        public LoginTwoFactorPage(string email, FullLoginResult result, TwoFactorProviderType? type = null)
            : base(updateActivity: false)
        {
            _deviceInfoService = Resolver.Resolve<IDeviceInfoService>();

            _email = email;
            _result = result;
            _masterPasswordHash = result.MasterPasswordHash;
            _key = result.Key;
            _providers = result.TwoFactorProviders;
            _providerType = type ?? GetDefaultProvider();

            _authService = Resolver.Resolve<IAuthService>();
            _userDialogs = Resolver.Resolve<IUserDialogs>();
            _syncService = Resolver.Resolve<ISyncService>();
            _googleAnalyticsService = Resolver.Resolve<IGoogleAnalyticsService>();
            _twoFactorApiRepository = Resolver.Resolve<ITwoFactorApiRepository>();
            _pushNotification = Resolver.Resolve<IPushNotification>();

            Init();
        }

        public FormEntryCell TokenCell { get; set; }
        public ExtendedSwitchCell RememberCell { get; set; }

        private void Init()
        {
            SubscribeYubiKey(true);
            if(_providers.Count > 1)
            {
                var sendEmailTask = SendEmailAsync(false);
            }

            ToolbarItems.Clear();
            var scrollView = new ScrollView();

            var anotherMethodButton = new ExtendedButton
            {
                Text = "Use another two-step login method",
                Style = (Style)Application.Current.Resources["btn-primaryAccent"],
                Margin = new Thickness(15, 0, 15, 25),
                Command = new Command(() => AnotherMethodAsync()),
                Uppercase = false,
                BackgroundColor = Color.Transparent,
                VerticalOptions = LayoutOptions.Start
            };

            var instruction = new Label
            {
                LineBreakMode = LineBreakMode.WordWrap,
                Margin = new Thickness(15),
                HorizontalTextAlignment = TextAlignment.Center
            };

            RememberCell = new ExtendedSwitchCell
            {
                Text = "Remember me",
                On = false
            };

            if(!_providerType.HasValue)
            {
                instruction.Text = "No providers available.";

                var layout = new StackLayout
                {
                    Children = { instruction, anotherMethodButton },
                    Spacing = 0
                };

                scrollView.Content = layout;

                Title = "Login Unavailable";
                Content = scrollView;
            }
            else if(_providerType.Value == TwoFactorProviderType.Authenticator ||
                _providerType.Value == TwoFactorProviderType.Email)
            {
                var continueToolbarItem = new ToolbarItem(AppResources.Continue, null, async () =>
                {
                    var token = TokenCell?.Entry.Text.Trim().Replace(" ", "");
                    await LogInAsync(token);
                }, ToolbarItemOrder.Default, 0);

                var padding = Helpers.OnPlatform(
                    iOS: new Thickness(15, 20),
                    Android: new Thickness(15, 8),
                    WinPhone: new Thickness(15, 20));

                TokenCell = new FormEntryCell(AppResources.VerificationCode, useLabelAsPlaceholder: true,
                    imageSource: "lock", containerPadding: padding);

                TokenCell.Entry.Keyboard = Keyboard.Numeric;
                TokenCell.Entry.ReturnType = ReturnType.Go;

                var table = new TwoFactorTable(
                    new TableSection(" ")
                    {
                        TokenCell,
                        RememberCell
                    });

                var layout = new StackLayout
                {
                    Children = { instruction, table },
                    Spacing = 0
                };

                scrollView.Content = layout;

                switch(_providerType.Value)
                {
                    case TwoFactorProviderType.Authenticator:
                        instruction.Text = "Enter the 6 digit verification code from your authenticator app.";
                        layout.Children.Add(anotherMethodButton);
                        break;
                    case TwoFactorProviderType.Email:
                        var emailParams = _providers[TwoFactorProviderType.Email];
                        var redactedEmail = emailParams["Email"].ToString();

                        instruction.Text = $"Enter the 6 digit verification code that was emailed to {redactedEmail}.";
                        var resendEmailButton = new ExtendedButton
                        {
                            Text = "Send verification code email again",
                            Style = (Style)Application.Current.Resources["btn-primaryAccent"],
                            Margin = new Thickness(15, 0, 15, 0),
                            Command = new Command(async () => await SendEmailAsync(true)),
                            Uppercase = false,
                            BackgroundColor = Color.Transparent,
                            VerticalOptions = LayoutOptions.Start
                        };

                        layout.Children.Add(resendEmailButton);
                        layout.Children.Add(anotherMethodButton);
                        break;
                    default:
                        break;
                }

                ToolbarItems.Add(continueToolbarItem);
                Title = AppResources.VerificationCode;

                Content = scrollView;
            }
            else if(_providerType == TwoFactorProviderType.Duo)
            {
                var duoParams = _providers[TwoFactorProviderType.Duo];

                var host = WebUtility.UrlEncode(duoParams["Host"].ToString());
                var req = WebUtility.UrlEncode(duoParams["Signature"].ToString());

                var webView = new HybridWebView
                {
                    Uri = $"http://192.168.1.6:4001/duo-mobile.html?host={host}&request={req}",
                    HorizontalOptions = LayoutOptions.FillAndExpand,
                    VerticalOptions = LayoutOptions.FillAndExpand,
                    MinimumHeightRequest = 400
                };
                webView.RegisterAction(async (sig) =>
                {
                    await LogInAsync(sig);
                });

                var table = new TwoFactorTable(
                    new TableSection(" ")
                    {
                        RememberCell
                    });

                var layout = new StackLayout
                {
                    Children = { webView, table, anotherMethodButton },
                    Spacing = 0
                };

                scrollView.Content = layout;

                Title = "Duo";
                Content = scrollView;
            }
            else if(_providerType == TwoFactorProviderType.YubiKey)
            {
                instruction.Text = "Hold your YubiKey NEO against the back of the device to continue.";

                var image = new CachedImage
                {
                    Source = "yubikey",
                    VerticalOptions = LayoutOptions.Start,
                    HorizontalOptions = LayoutOptions.Center,
                    WidthRequest = 266,
                    HeightRequest = 160,
                    Margin = new Thickness(0, 0, 0, 25)
                };

                var table = new TwoFactorTable(
                    new TableSection(" ")
                    {
                        RememberCell
                    });

                var layout = new StackLayout
                {
                    Children = { instruction, image, table, anotherMethodButton },
                    Spacing = 0
                };

                scrollView.Content = layout;

                Title = "YubiKey";
                Content = scrollView;
            }
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            ListenYubiKey(true);

            if(TokenCell != null)
            {
                TokenCell.InitEvents();
                TokenCell.Entry.FocusWithDelay();
                TokenCell.Entry.Completed += Entry_Completed;
            }
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            ListenYubiKey(false);

            if(TokenCell != null)
            {
                TokenCell.Dispose();
                TokenCell.Entry.Completed -= Entry_Completed;
            }
        }

        private async void AnotherMethodAsync()
        {
            var beforeProviderType = _providerType;

            var options = new List<string>();
            if(_providers.ContainsKey(TwoFactorProviderType.Authenticator))
            {
                options.Add("Authenticator App");
            }

            if(_providers.ContainsKey(TwoFactorProviderType.Duo))
            {
                options.Add("Duo");
            }

            if(_providers.ContainsKey(TwoFactorProviderType.YubiKey))
            {
                var nfcKey = _providers[TwoFactorProviderType.YubiKey].ContainsKey("Nfc") &&
                    (bool)_providers[TwoFactorProviderType.YubiKey]["Nfc"];
                if(_deviceInfoService.NfcEnabled || nfcKey)
                {
                    options.Add("YubiKey NFC Security Key");
                }
            }

            if(_providers.ContainsKey(TwoFactorProviderType.Email))
            {
                options.Add("Email");
            }

            options.Add("Recovery Code");

            var selection = await DisplayActionSheet("Two-step Login Options", AppResources.Cancel, null, options.ToArray());
            if(selection == "Authenticator App")
            {
                _providerType = TwoFactorProviderType.Authenticator;
            }
            else if(selection == "Duo")
            {
                _providerType = TwoFactorProviderType.Duo;
            }
            else if(selection == "YubiKey NFC Security Key")
            {
                _providerType = TwoFactorProviderType.YubiKey;
            }
            else if(selection == "Email")
            {
                _providerType = TwoFactorProviderType.Email;
            }
            else if(selection == "Recovery Code")
            {
                Device.OpenUri(new Uri("https://help.bitwarden.com/article/lost-two-step-device/"));
                return;
            }

            if(beforeProviderType != _providerType)
            {
                Init();
                ListenYubiKey(false, beforeProviderType == TwoFactorProviderType.YubiKey);
                ListenYubiKey(true);
            }
        }

        private async Task SendEmailAsync(bool doToast)
        {
            if(_providerType != TwoFactorProviderType.Email)
            {
                return;
            }

            var response = await _twoFactorApiRepository.PostSendEmailLoginAsync(new Models.Api.TwoFactorEmailRequest
            {
                Email = _email,
                MasterPasswordHash = _masterPasswordHash
            });

            if(response.Succeeded && doToast)
            {
                _userDialogs.Toast("Verification email sent.");
            }
            else if(!response.Succeeded)
            {
                _userDialogs.Alert("Could not send verification email. Try again.");
            }
        }

        private async void Entry_Completed(object sender, EventArgs e)
        {
            var token = TokenCell.Entry.Text.Trim().Replace(" ", "");
            await LogInAsync(token);
        }

        private async Task LogInAsync(string token)
        {
            if(!_providerType.HasValue || _lastAction.LastActionWasRecent())
            {
                return;
            }
            _lastAction = DateTime.UtcNow;

            if(string.IsNullOrWhiteSpace(token))
            {
                await DisplayAlert(AppResources.AnErrorHasOccurred, string.Format(AppResources.ValidationFieldRequired,
                    AppResources.VerificationCode), AppResources.Ok);
                return;
            }

            _userDialogs.ShowLoading(string.Concat(AppResources.Validating, "..."), MaskType.Black);
            var response = await _authService.TokenPostTwoFactorAsync(_providerType.Value, token, RememberCell.On,
                _email, _masterPasswordHash, _key);
            _userDialogs.HideLoading();
            if(!response.Success)
            {
                ListenYubiKey(true);
                await DisplayAlert(AppResources.AnErrorHasOccurred, response.ErrorMessage, AppResources.Ok);
                return;
            }

            _googleAnalyticsService.TrackAppEvent("LoggedIn From Two-step", _providerType.Value.ToString());

            if(Device.RuntimePlatform == Device.Android)
            {
                _pushNotification.Register();
            }

            var task = Task.Run(async () => await _syncService.FullSyncAsync(true));

            Device.BeginInvokeOnMainThread(() =>
            {
                Application.Current.MainPage = new MainPage();
            });
        }

        private TwoFactorProviderType? GetDefaultProvider()
        {
            TwoFactorProviderType? provider = null;

            if(_providers != null)
            {
                foreach(var p in _providers)
                {
                    switch(p.Key)
                    {
                        case TwoFactorProviderType.Authenticator:
                            if(provider == TwoFactorProviderType.Duo || provider == TwoFactorProviderType.YubiKey)
                            {
                                continue;
                            }
                            break;
                        case TwoFactorProviderType.Email:
                            if(provider.HasValue)
                            {
                                continue;
                            }
                            break;
                        case TwoFactorProviderType.Duo:
                            if(provider == TwoFactorProviderType.YubiKey)
                            {
                                continue;
                            }
                            break;
                        case TwoFactorProviderType.YubiKey:
                            var nfcKey = p.Value.ContainsKey("Nfc") && (bool)p.Value["Nfc"];
                            if(!_deviceInfoService.NfcEnabled || !nfcKey)
                            {
                                continue;
                            }
                            break;
                        default:
                            continue;
                    }

                    provider = p.Key;
                }
            }

            return provider;
        }

        private void ListenYubiKey(bool listen, bool overrideCheck = false)
        {
            if(_providerType == TwoFactorProviderType.YubiKey || overrideCheck)
            {
                MessagingCenter.Send(Application.Current, "ListenYubiKeyOTP", listen);
            }
        }

        private void SubscribeYubiKey(bool subscribe)
        {
            if(_providerType != TwoFactorProviderType.YubiKey)
            {
                return;
            }

            MessagingCenter.Unsubscribe<Application, string>(Application.Current, "GotYubiKeyOTP");
            MessagingCenter.Unsubscribe<Application>(Application.Current, "ResumeYubiKey");
            if(!subscribe)
            {
                return;
            }

            MessagingCenter.Subscribe<Application, string>(Application.Current, "GotYubiKeyOTP", async (sender, otp) =>
            {
                MessagingCenter.Unsubscribe<Application, string>(Application.Current, "GotYubiKeyOTP");
                if(_providerType == TwoFactorProviderType.YubiKey)
                {
                    await LogInAsync(otp);
                }
            });

            SubscribeYubiKeyResume();
        }

        private void SubscribeYubiKeyResume()
        {
            MessagingCenter.Subscribe<Application>(Application.Current, "ResumeYubiKey", (sender) =>
            {
                MessagingCenter.Unsubscribe<Application>(Application.Current, "ResumeYubiKey");
                if(_providerType == TwoFactorProviderType.YubiKey)
                {
                    MessagingCenter.Send(Application.Current, "ListenYubiKeyOTP", true);
                    SubscribeYubiKeyResume();
                }
            });
        }

        public class TwoFactorTable : ExtendedTableView
        {
            public TwoFactorTable(TableSection section)
            {
                Intent = TableIntent.Settings;
                EnableScrolling = false;
                HasUnevenRows = true;
                EnableSelection = true;
                NoFooter = true;
                NoHeader = true;
                VerticalOptions = LayoutOptions.Start;
                Root = Root = new TableRoot
                {
                    section
                };

                if(Device.RuntimePlatform == Device.iOS)
                {
                    RowHeight = -1;
                    EstimatedRowHeight = 70;
                }
            }
        }
    }
}
