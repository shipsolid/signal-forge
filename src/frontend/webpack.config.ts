import type { Configuration } from 'webpack';
// @grafana/faro-webpack-plugin exports the class as the CJS default export
// eslint-disable-next-line @typescript-eslint/no-require-imports
const FaroSourceMapUploaderPlugin = require('@grafana/faro-webpack-plugin');

// The Faro source map uploader runs at the end of every production build and
// ships the generated .js.map files to Grafana Cloud so that stack traces in
// Faro error events are deobfuscated automatically.
//
// Required environment variables (set in CI, not locally):
//   FARO_API_KEY  — Grafana Cloud stack API key with "sourcemaps:write" scope
//
// If FARO_API_KEY is absent the plugin is skipped so local / watch builds work
// without credentials.

const faroPlugins = process.env['FARO_API_KEY']
  ? [
      new FaroSourceMapUploaderPlugin({
        appName: 'signal-forge',
        endpoint: 'https://faro-api-prod-us-central-7.grafana.net/faro/api/v1',
        appId: '128',
        stackId: '1589094',
        apiKey: process.env['FARO_API_KEY'],
        gzipContents: true,
        verbose: true,
      }),
    ]
  : [];

const config: Configuration = {
  plugins: faroPlugins,
};

export default config;
