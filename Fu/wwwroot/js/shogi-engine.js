// YaneuraOu WASM Engine - Minimal Bootstrap
// ロジックはC#側 (ShogiEngineService) で管理

let engine = null;

window.ShogiEngine = {
    // スクリプトをロードしてエンジンを初期化
    init: async function (dotNetRef) {
        if (engine) {
            return true;
        }

        try {
            // YaneuraOuスクリプトをロード
            const script = document.createElement('script');
            script.src = 'lib/yaneuraou/yaneuraou.js';
            document.head.appendChild(script);

            await new Promise((resolve, reject) => {
                script.onload = resolve;
                script.onerror = reject;
            });

            // YaneuraOuモジュールを初期化
            engine = await YaneuraOu({
                locateFile: (path) => `lib/yaneuraou/${path}`
            });

            // メッセージリスナーを設定（C#にコールバック）
            engine.addMessageListener((line) => {
                console.log('Engine:', line);
                dotNetRef.invokeMethodAsync('OnEngineMessage', line);
            });

            console.log('YaneuraOu engine initialized');
            return true;
        } catch (error) {
            console.error('Failed to initialize shogi engine:', error);
            return false;
        }
    },

    // コマンドを送信
    postMessage: function (command) {
        if (engine) {
            engine.postMessage(command);
            return true;
        }
        return false;
    },

    // Cross-Origin Isolation チェック（SharedArrayBuffer に必要）
    isCrossOriginIsolated: function () {
        return window.crossOriginIsolated === true;
    }
};
