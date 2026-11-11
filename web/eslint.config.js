import intlify from '@intlify/eslint-plugin-vue-i18n'
import prettier from 'eslint-config-prettier'
import vue from 'eslint-plugin-vue'
import tseslint from 'typescript-eslint'

export default tseslint.config(
  { ignores: ['dist', 'playwright-report', 'test-results'] },
  ...tseslint.configs.recommended,
  ...vue.configs['flat/recommended'],
  ...intlify.configs['flat/recommended'],
  {
    files: ['**/*.vue'],
    languageOptions: { parserOptions: { parser: tseslint.parser } },
  },
  {
    settings: {
      'vue-i18n': {
        localeDir: './src/locales/*.json',
        messageSyntaxVersion: '^11.0.0',
      },
    },
    rules: {
      // Hard-coded user-facing strings are not allowed: everything goes through vue-i18n.
      '@intlify/vue-i18n/no-raw-text': 'error',
      '@intlify/vue-i18n/no-missing-keys': 'error',
      // export.readme.* is read by the server (built into it), which writes the README of an export in the person's language.
      '@intlify/vue-i18n/no-unused-keys': [
        'error',
        { extensions: ['.ts', '.vue'], ignores: ['/^export\\.readme\\./'] },
      ],
      '@intlify/vue-i18n/no-missing-keys-in-other-locales': 'error',
      '@intlify/vue-i18n/no-v-html': 'error',
    },
  },
  prettier,
)
