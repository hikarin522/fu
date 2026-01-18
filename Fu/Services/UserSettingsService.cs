using Microsoft.JSInterop;

using R3;

using Fu.Core.Abstractions;

namespace Fu.Services;

/// <summary>
/// ユーザー設定の管理サービス
/// 通知音、その他ユーザー設定の永続化を担当
/// </summary>
public class UserSettingsService(IJSRuntime js, IStorageService storage) : IDisposable
{
    private const string SoundEnabledKey = "sound_enabled";
    private const string NicknameKey = "nickname";

    private readonly Subject<Unit> _settingsChanged = new();

    /// <summary>通知音が有効か</summary>
    public bool SoundEnabled { get; private set; } = true;

    /// <summary>ニックネーム</summary>
    public string Nickname { get; private set; } = "";

    /// <summary>設定が変更された時</summary>
    public Observable<Unit> SettingsChanged => this._settingsChanged;

    /// <summary>
    /// 設定を読み込む
    /// </summary>
    public async Task LoadAsync()
    {
        this.SoundEnabled = await this.LoadSoundSettingAsync();
        this.Nickname = await storage.GetAsync<string>(NicknameKey) ?? "";
    }

    /// <summary>
    /// ニックネームを保存
    /// </summary>
    public async Task SaveNicknameAsync(string nickname)
    {
        this.Nickname = nickname;
        await storage.SetAsync(NicknameKey, nickname);
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
            await js.InvokeVoidAsync("TurnNotification.play");
        }
        catch {
            // 音声再生に失敗しても無視
        }
    }

    #region 永続化

    private async Task<bool> LoadSoundSettingAsync()
    {
        var value = await storage.GetAsync<bool?>(SoundEnabledKey);
        return value ?? true; // デフォルトはtrue
    }

    private async Task SaveSoundSettingAsync(bool enabled) =>
        await storage.SetAsync(SoundEnabledKey, enabled);

    #endregion

    public void Dispose()
    {
        this._settingsChanged.Dispose();
        GC.SuppressFinalize(this);
    }
}
