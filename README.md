# Hapbeat Unity SDK の iOS 動作確認プロジェクト

Hapbeat を iOS のアプリで使うことを検討している方が、ご自身の iPhone と Hapbeat の機器で動作を確かめるための Unity プロジェクトです。StreamClip の再生と、機器に入っている Kit の再生をボタンで試せます。

使っている SDK は v1.0.1 です。v0.5.0 から v1.0.0 までの SDK は、iOS で StreamClip を使うと落ちます（[iOS で落ちる問題](#ios-で落ちる問題)）。iOS では 1.0.1 以降をお使いください。

## 確認した環境

| 項目 | 版 |
|---|---|
| iPhone | iPhone 17 Pro Max、iOS 26.6.2 |
| Mac | macOS 26.4.1、Xcode 26.6 |
| Unity | 6000.0.59f2 |
| SDK | 1.0.1 の公開前の commit（`bdf2762`。コードは 1.0.1 と同じ） |
| 機器のファームウェア | v1.0.0 の開発版（1.0.0d49） |
| 署名 | 無料の Apple ID（Personal Team） |
| ビルドの構成 | ReleaseForRunning（Xcode の Run の既定）、Release、Debug |

この環境で、機器の発見、StreamClip の再生、Kit の再生のどれでも、アプリが落ちずに機器が振動することを確かめました。

**確かめていないこと:**

- **TestFlight・App Store 用の署名:** Apple の文書は、ブロードキャストの送信に multicast の entitlement（`com.apple.developer.networking.multicast`）を求めています。上の環境では無くても機器が見つかりましたが、理由は分かっていません。配布を予定している場合は、配布用の署名でもお確かめください。
- **公開版のファームウェア:** 確認には v1.0.0 の開発版を使いました。公開版の 1.0.0 と、その前の 0.5.0 の機器では、iPhone からの再生を試していません。0.5.0 とは、通信の形式が合うことをコードで確かめました。
- **ほかの iPhone と iOS の版**
- **長時間の使用での発熱と電池の消費**

## 必要なもの

- Mac と Xcode（iPhone の iOS に対応する版。Unity 6 には Xcode 15 以上が必要です）
- Unity 6000.0 系と iOS Build Support
- Git（Unity が GitHub から SDK を取得するときに使います）
- iPhone（設定の「プライバシーとセキュリティ」で「デベロッパモード」をオン）
- Apple ID（動作確認だけなら無料で構いません。無料の署名のアプリは 7 日で期限が切れます）
- Hapbeat の機器（iPhone と同じ Wi-Fi に接続。ファームウェアは最新の公開版を推奨。[Hapbeat Studio](https://studio.hapbeat.com/) から更新できます）

## ビルドの手順

1. このプロジェクトを取得します（`git clone https://github.com/hapbeat/hapbeat-unity-ios-sample.git`、または GitHub の Code > Download ZIP）。
2. `UnityProject` を Unity Hub の Add で追加し、6000.0 系の Editor で開きます。
3. File > Build Profiles で iOS を選び、Switch Platform を押します。
4. Edit > Project Settings > Player の iOS のタブで、Other Settings の Bundle Identifier をご自身の値に変えます（`com.yourcompany.hapbeatcheck` のままでは署名できません）。
5. Build Profiles の Build を押し、書き出し先のフォルダを選びます。
6. 書き出された `Unity-iPhone.xcodeproj` を Xcode で開き、Unity-iPhone のターゲットの Signing & Capabilities で Team を選びます。
7. iPhone をケーブルでつないで選び、Run を押します。
8. 初回に「信頼されていない開発元」と出たら、iPhone の設定 > 一般 > VPN とデバイス管理で開発元を信頼して、もう一度 Run を押します。
9. ローカルネットワークの許可が出たら「許可」を押します。

## 確認すること

アプリの音は鳴りません。振動の強さは 0.3 です。

| 操作 | 期待する結果 |
|---|---|
| 起動して待つ | 画面の上に `Devices: 1` と機器の IP が出ます |
| `Clip (once)` を押す | 落ちずに 1 回振動します（StreamClip） |
| Kit の一覧のイベントを押す | そのイベントが振動します（Kit の `Play`） |
| `Stop` を押す | 再生が止まります |

Kit の一覧が空のときは、機器に Kit が入っていません。Hapbeat Studio から入れてください。

## うまくいかないとき

- **`Devices` が出ない:** 同じ Wi-Fi か、iPhone の設定 > プライバシーとセキュリティ > ローカルネットワークでアプリが許可されているかを確かめてください。画面の `Device IP` の欄に機器の IP を入れると、その IP へ直接探しに行きます。
- **無視してよいログ:** 起動直後の `Discovery send to 255.255.255.255 failed (HostUnreachable)` と `Discovery send to 169.254.255.255 failed`。機器が答えた後は別の宛先へ送ります。
- **落ちたとき:** この repo の [Issues](https://github.com/hapbeat/hapbeat-unity-ios-sample/issues) に、次の 2 つを添えてご連絡ください。どちらも Xcode の Window > Devices and Simulators から取れます。ログにはお使いのネットワークの IP が含まれます。
  - アプリの Documents フォルダの `hb-check.log`（アプリを選んで Download Container）
  - クラッシュログ（Open Recent Logs。古い Xcode では View Device Logs）

## お使いのアプリへの導入

`Packages/manifest.json` の SDK の指定を、次の値に変えます。

```json
"com.hapbeat.sdk": "https://github.com/hapbeat/hapbeat-unity-sdk.git#v1.0.1"
```

- SDK 0.6.0 から上げる場合は、[CHANGELOG](https://github.com/hapbeat/hapbeat-unity-sdk/blob/v1.0.1/CHANGELOG.md) の 1.0.0 をご確認ください。主な変更は、Kit の素材の強さの名前（`intensity` から `clip_gain`）と、`HapbeatConfig.hapticDelaySeconds` の削除（`HapbeatAvSync` へ移動）です。
- SDK が対応する Unity は 6000.0 以降です。
- ローカルネットワークの許可の説明文は、`Assets/Editor/HbCheckBuild.cs` の `AddLocalNetworkUsage` をそのまま使えます。

## iOS で落ちる問題

- **症状:** iOS で StreamClip を再生中に機器が見つかると、スレッド `HapbeatEndpointStreamMixer` が `EXC_BREAKPOINT (code=1)` で停止します。機器が無いときは落ちません。
- **原因:** StreamClip の送信を受け持つスレッドが、診断用の計測に `GC.GetAllocatedBytesForCurrentThread()` を呼んでいました。この関数は Unity の IL2CPP に実装が無く、iOS 向けのビルドでは呼んだ瞬間に停止します。例外ではないので `try`/`catch` では防げません。Android では停止せずに動いて見えていました。
- **Kit の再生（`Play`）:** このスレッドを使わないので落ちません。
- **修正:** v1.0.1 で、IL2CPP のビルドではこの計測を行わないようにしました。再生には影響しません。
- **該当する版:** v0.5.0 から v1.0.0 まで。v0.5.0、v0.6.0、v1.0.0 に同じ呼び出しがあることをコードで確かめました。iPhone で再現を確かめたのは、修正の 1 つ前の開発版（`84d4655`）です。

## ライセンス

[MIT License](LICENSE)
