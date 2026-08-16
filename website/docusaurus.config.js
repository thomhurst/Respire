// @ts-check
import {themes as prismThemes} from 'prism-react-renderer';

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
  scripts: [{src: 'https://tluma.ai/widget.js', async: true}],
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
        {name: 'theme-color', content: '#091b1a'},
        {name: 'keywords', content: 'Redis, Valkey, RESP, .NET, C#, async, client'},
      ],
      colorMode: {defaultMode: 'dark', respectPrefersColorScheme: true},
      navbar: {
        title: 'Respire',
        logo: {alt: 'Respire logo', src: 'img/logo.svg'},
        items: [
          {type: 'docSidebar', sidebarId: 'docsSidebar', position: 'left', label: 'Docs'},
          {to: '/docs/guides/blocking-queues', label: 'Guides', position: 'left'},
          {to: '/docs/performance', label: 'Performance', position: 'left'},
          {href: 'https://www.nuget.org/packages/Respire', label: 'NuGet', position: 'right'},
          {href: 'https://github.com/thomhurst/Respire', label: 'GitHub', position: 'right', className: 'navbar-github-link'},
          {href: 'https://github.com/sponsors/thomhurst', label: '❤️ Sponsor', position: 'right'},
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
        copyright: `Respire · Built in the open · ${new Date().getFullYear()}`,
      },
      prism: {
        theme: prismThemes.github,
        darkTheme: prismThemes.dracula,
        additionalLanguages: ['csharp', 'bash', 'json'],
      },
    }),
};

export default config;
