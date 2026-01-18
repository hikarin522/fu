using Microsoft.JSInterop;

using R3;

namespace Fu.Services;

/// <summary>
/// ユーザー設定の管理サービス
/// 通知音、その他ユーザー設定の永続化を担当
/// </summary>
public class UserSettingsService : IDisposable
{
    private readonly IJSRuntime _js;
    private readonly Subject<Unit> _settingsChanged = new();

    /// <summary>通知音が有効か</summary>
    public bool SoundEnabled { get; private set; } = true;

    /// <summary>設定が変更された時</summary>
    public Observable<Unit> SettingsChanged => this._settingsChanged;

    public UserSettingsService(IJSRuntime js)
    {
        this._js = js;
    }

    /// <summary>
    /// 設定を読み込む
    /// </summary>
    public async Task LoadAsync()
    {
        this.SoundEnabled = await this.LoadSoundSettingAsync();
    }

    /// <summary>
    /// 通知音設定を切り替え
    /// </summary>
    public async Task ToggleSoundAsync()
    {
        this.SoundEnabled = !this.SoundEnabled;
        await this.SaveSoundSettingAsync(this.SoundEnabled);
        this._settingsChanged.OnNext(Unit.Default);
    }

    /// <summary>
    /// 手番通知音を再生
    /// </summary>
    public async Task PlayTurnNotificationAsync()
    {
        if (!this.SoundEnabled) {
            return;
        }

        try {
            await this._js.InvokeVoidAsync("TurnNotification.play");
        }
        catch {
            // 音声再生に失敗しても無視
        }
    }

    #region 永続化

    private async Task<bool> LoadSoundSettingAsync()
    {
        try {
            var value = await this._js.InvokeAsync<string?>("SoundSettings.load");
            return value != "false"; // デフォルトはtrue
        }
        catch {
            return true;
        }
    }

    private async Task SaveSoundSettingAsync(bool enabled)
    {
        try {
            await this._js.InvokeVoidAsync("SoundSettings.save", enabled ? "true" : "false");
        }
        catch {
            // 保存失敗は無視
        }
    }

    #endregion

    public void Dispose()
    {
        this._settingsChanged.Dispose();
        GC.SuppressFinalize(this);
    }
}
