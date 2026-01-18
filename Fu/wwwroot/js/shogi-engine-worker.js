// YaneuraOu WASM Engine Worker
// メインスレッドから隔離してエンジンを実行し、クラッシュ時の影響を防ぐ

let engine = null;
let isSearching = false;

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

        // メッセージリスナーを設定
        yaneuraou.addMessageListener((line) => {
            if (line.startsWith('bestmove ')) {
                isSearching = false;
            }
            self.postMessage({ type: 'message', data: line });
        });

        engine = yaneuraou;

        // USIハンドシェイク
        await new Promise((resolve) => {
            const handler = (line) => {
                if (line === 'usiok') {
                    engine.removeMessageListener(handler);
                    resolve();
                }
            };
            engine.addMessageListener(handler);
            engine.postMessage('usi');
        });

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

        case 'evaluate':
            if (engine) {
                if (isSearching) {
                    engine.postMessage('stop');
                }
                isSearching = true;
                engine.postMessage('position sfen ' + data.sfen);
                engine.postMessage(data.depth > 0 ? `go depth ${data.depth}` : 'go infinite');
            }
            break;

        case 'stop':
            if (engine && isSearching) {
                engine.postMessage('stop');
            }
            break;
    }
};

// エラーハンドリング
self.onerror = (error) => {
    console.error('Worker error:', error);
    self.postMessage({ type: 'crash', data: error.message });
};
