// @ts-check
import {respireDark, respireLight} from './src/prism-themes.js';

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'Respire',
  tagline: 'Redis, at the speed of modern .NET',
  favicon: 'img/favicon.svg',
  url: 'https://thomhurst.github.io',
  baseUrl: '/Respire/',
  organizationName: 'thomhurst',
  projectName: 'Respire',
  onBrokenLinks: 'throw',
  markdown: {
    hooks: {onBrokenMarkdownLinks: 'throw'},
  },
  i18n: {defaultLocale: 'en', locales: ['en']},
  headTags: [
    {
      tagName: 'meta',
      attributes: {name: 'algolia-site-verification', content: '17557AF2E465C739'},
    },
    {
      tagName: 'script',
      attributes: {},
      innerHTML: `window.tlumaConfig = {
  source: "thomhurst/respire",
  theme: "auto",
  brandColor: "blue",
  button: "bottom-right",
  welcomePulse: true,
  edgePadding: "1rem",
  autoOpen: false,
  desktopFullscreenByDefault: false
};`,
    },
  ],
  stylesheets: [
    {rel: 'preconnect', href: 'https://fonts.googleapis.com'},
    {rel: 'preconnect', href: 'https://fonts.gstatic.com', crossorigin: 'anonymous'},
    {
      rel: 'stylesheet',
      href: 'https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500;600&family=Martian+Mono:wdth,wght@75..112.5,100..800&family=Schibsted+Grotesk:ital,wght@0,400..900;1,400..900&display=swap',
    },
  ],
  scripts: [{src: 'https://tluma.ai/widget.js', async: true}],
  clientModules: ['./src/client/resp-headings.js'],
  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          sidebarPath: './sidebars.js',
          editUrl: 'https://github.com/thomhurst/Respire/tree/main/website/',
          showLastUpdateTime: true,
        },
        blog: false,
        theme: {customCss: './src/css/custom.css'},
        sitemap: {changefreq: 'weekly', priority: 0.5},
      }),
    ],
  ],
  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      metadata: [
        {name: 'theme-color', content: '#f7f7f5'},
        {name: 'keywords', content: 'Redis, Valkey, RESP, .NET, C#, async, client'},
      ],
      colorMode: {defaultMode: 'light', respectPrefersColorScheme: true},
      navbar: {
        title: 'Respire',
        logo: {alt: 'Respire logo', src: 'img/logo.svg', srcDark: 'img/logo-dark.svg'},
        items: [
          {type: 'docSidebar', sidebarId: 'docsSidebar', position: 'left', label: 'Docs'},
          {to: '/docs/guides/blocking-queues', label: 'Guides', position: 'left'},
          {to: '/docs/performance', label: 'Performance', position: 'left'},
          {href: 'https://www.nuget.org/packages/Respire', label: 'NuGet', position: 'right'},
          {href: 'https://github.com/thomhurst/Respire', label: 'GitHub', position: 'right'},
          {href: 'https://github.com/sponsors/thomhurst', label: 'Sponsor', position: 'right'},
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Learn',
            items: [
              {label: 'Getting started', to: '/docs/getting-started'},
              {label: 'Commands', to: '/docs/commands/strings-and-keys'},
              {label: 'Integrations', to: '/docs/integrations/dependency-injection'},
              {label: 'From StackExchange.Redis', to: '/docs/stackexchange-redis'},
            ],
          },
          {
            title: 'Project',
            items: [
              {label: 'GitHub', href: 'https://github.com/thomhurst/Respire'},
              {label: 'Roadmap', to: '/docs/roadmap'},
              {label: 'MIT license', href: 'https://github.com/thomhurst/Respire#license'},
            ],
          },
        ],
        copyright: `Respire is MIT licensed and built in the open. © ${new Date().getFullYear()} Tom Longhurst.`,
      },
      prism: {
        theme: respireLight,
        darkTheme: respireDark,
        additionalLanguages: ['csharp', 'bash', 'json'],
      },
    }),
};

export default config;
