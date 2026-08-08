/** @type {import('@docusaurus/plugin-content-docs').SidebarsConfig} */
const sidebars = {
  docsSidebar: [
    'intro',
    'getting-started',
    {
      type: 'category',
      label: 'Core concepts',
      items: ['fundamentals/connections', 'fundamentals/values-and-serialization'],
    },
    {
      type: 'category',
      label: 'Commands',
      items: ['commands/strings-and-keys', 'commands/collections'],
    },
    {
      type: 'category',
      label: 'Guides',
      items: [
        'guides/blocking-queues',
        'guides/pub-sub',
        'guides/batches-and-transactions',
        'guides/raw-commands',
      ],
    },
    {
      type: 'category',
      label: 'Integrations',
      items: ['integrations/dependency-injection', 'integrations/caching', 'integrations/observability'],
    },
    'performance',
    'roadmap',
  ],
};

export default sidebars;
