import { defineConfig } from 'vitepress'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'

// ルート（main 由来）の package.json からバージョンを取得する。
// リリース Action が version をバンプし、それが docs ブランチへマージされると
// ここに自動で反映される（ナビのバージョン表記が main に追従する）。
const pkg = JSON.parse(
  readFileSync(fileURLToPath(new URL('../../package.json', import.meta.url)), 'utf-8')
)

// Tabstep ドキュメントサイト設定（日本語）
export default defineConfig({
  lang: 'ja-JP',
  title: 'Tabstep',
  description: 'Unity の Project ウィンドウにタブを追加するエディタ拡張 Tabstep のドキュメント',
  lastUpdated: true,
  cleanUrls: true,
  head: [
    ['meta', { name: 'theme-color', content: '#3c8772' }],
    ['meta', { property: 'og:type', content: 'website' }],
    ['meta', { property: 'og:title', content: 'Tabstep ドキュメント' }],
    ['meta', {
      property: 'og:description',
      content: 'Unity の Project ウィンドウにタブを追加するエディタ拡張 Tabstep のドキュメント'
    }]
  ],

  themeConfig: {
    nav: [
      { text: 'ガイド', link: '/guide/', activeMatch: '/guide/' },
      { text: '機能', link: '/features/', activeMatch: '/features/' },
      { text: `v${pkg.version}`, link: 'https://github.com/YozoraKurage/Tabstep/releases' }
    ],

    sidebar: {
      '/guide/': [
        {
          text: 'はじめに',
          items: [
            { text: 'Tabstep とは', link: '/guide/' },
            { text: 'インストール', link: '/guide/installation' },
            { text: 'クイックスタート', link: '/guide/getting-started' }
          ]
        },
        {
          text: '使い方',
          items: [
            { text: '画面構成', link: '/guide/interface' },
            { text: '設定 (Preferences)', link: '/guide/settings' },
            { text: 'よくある質問', link: '/guide/faq' }
          ]
        }
      ],
      '/features/': [
        {
          text: '機能一覧',
          items: [
            { text: '概要', link: '/features/' },
            { text: 'タブ・ピン・ワークスペース', link: '/features/tabs' },
            { text: 'ナビゲーションと履歴', link: '/features/navigation' },
            { text: 'タブごとの検索', link: '/features/search' },
            { text: 'Tabstep Shelf', link: '/features/shelf' },
            { text: 'クイックアクセス', link: '/features/quick-access' },
            { text: 'アセット操作', link: '/features/asset-management' },
            { text: 'Harmony 連携', link: '/features/harmony' }
          ]
        }
      ]
    },

    outline: { label: '目次', level: [2, 3] },
    docFooter: { prev: '前へ', next: '次へ' },
    darkModeSwitchLabel: '外観',
    lightModeSwitchTitle: 'ライトモードに切り替え',
    darkModeSwitchTitle: 'ダークモードに切り替え',
    sidebarMenuLabel: 'メニュー',
    returnToTopLabel: 'トップに戻る',
    lastUpdated: {
      text: '最終更新',
      formatOptions: { dateStyle: 'medium' }
    },

    search: {
      provider: 'local',
      options: {
        translations: {
          button: { buttonText: '検索', buttonAriaLabel: '検索' },
          modal: {
            noResultsText: '一致する結果が見つかりませんでした',
            resetButtonTitle: '検索をリセット',
            footer: {
              selectText: '選択',
              navigateText: '移動',
              closeText: '閉じる'
            }
          }
        }
      }
    },

    socialLinks: [
      { icon: 'github', link: 'https://github.com/YozoraKurage/Tabstep' }
    ],

    editLink: {
      pattern: 'https://github.com/YozoraKurage/Tabstep/edit/docs/docs/:path',
      text: 'このページを編集'
    },

    footer: {
      message: 'MIT License のもとで公開されています。',
      copyright: 'Copyright © 2026 Yozolab'
    }
  }
})
