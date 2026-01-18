// YaneuraOu WASM Engine Worker
// 純粋な転送層 - WASMエンジンとメインスレッド間のメッセージを中継するだけ

let engine = null;

// エンジン初期化
async function initEngine() {
    if (engine) {
        return true;
    }

    try {
        // YaneuraOuスクリプトをインポート
        importScripts('lib/yaneuraou/yaneuraou.js');

        // WASMモジュールを初期化
        const yaneuraou = await YaneuraOu({
            locateFile: (path) => `lib/yaneuraou/${path}`
        });

        // メッセージリスナーを設定（全てのメッセージをそのまま転送）
        yaneuraou.addMessageListener((line) => {
            self.postMessage({ type: 'message', data: line });
        });

        engine = yaneuraou;
        return true;
    } catch (error) {
        console.error('Worker: Failed to initialize engine:', error);
        self.postMessage({ type: 'error', data: error.message });
        return false;
    }
}

// メインスレッドからのメッセージを処理
self.onmessage = async (e) => {
    const { type, data } = e.data;

    switch (type) {
        case 'init':
            const success = await initEngine();
            self.postMessage({ type: 'init', data: success });
            break;

        case 'command':
            if (engine) {
                engine.postMessage(data);
            }
            break;
    }
};

// エラーハンドリング
self.onerror = (error) => {
    console.error('Worker error:', error);
    self.postMessage({ type: 'crash', data: error.message });
};
