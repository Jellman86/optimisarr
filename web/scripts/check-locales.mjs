import fs from 'node:fs'
import ts from 'typescript'

const localeNames = ['de', 'es', 'fr', 'it', 'ja', 'pt', 'ru', 'zh']

function readLocale(name) {
  const path = `src/lib/i18n/${name}.ts`
  const source = ts.createSourceFile(
    path,
    fs.readFileSync(path, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  )
  const declaration = source.statements
    .flatMap((statement) =>
      ts.isVariableStatement(statement) ? [...statement.declarationList.declarations] : [],
    )
    .find((candidate) => candidate.name.getText(source) === name)

  if (!declaration?.initializer) throw new Error(`${path}: missing exported ${name} locale`)

  function value(node) {
    if (ts.isObjectLiteralExpression(node)) {
      return Object.fromEntries(
        node.properties
          .filter(ts.isPropertyAssignment)
          .map((property) => [property.name.getText(source), value(property.initializer)]),
      )
    }
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) return node.text
    throw new Error(`${path}: unsupported locale value at ${source.getLineAndCharacterOfPosition(node.pos).line + 1}`)
  }

  return value(declaration.initializer)
}

function flatten(value, prefix = '', result = {}) {
  for (const [key, child] of Object.entries(value)) {
    const path = prefix ? `${prefix}.${key}` : key
    if (typeof child === 'string') result[path] = child
    else flatten(child, path, result)
  }
  return result
}

function placeholders(value) {
  return [...value.matchAll(/\{\w+\}/g)].map((match) => match[0]).sort()
}

function looksLikeEnglishProse(value) {
  return value.length >= 32 && /[A-Za-z]{4,}\s+[A-Za-z]{4,}/.test(value)
}

const english = flatten(readLocale('en'))
const failures = []

for (const localeName of localeNames) {
  const locale = flatten(readLocale(localeName))
  for (const [key, source] of Object.entries(english)) {
    const expected = placeholders(source)
    const actual = placeholders(locale[key] ?? '')
    if (expected.join('\0') !== actual.join('\0')) {
      failures.push(`${localeName}.${key}: expected [${expected}], found [${actual}]`)
    }
    if (locale[key] === source && looksLikeEnglishProse(source)) {
      failures.push(`${localeName}.${key}: untranslated English prose`)
    }
  }
}

if (failures.length > 0) {
  console.error(`Locale audit failed:\n${failures.join('\n')}`)
  process.exit(1)
}

console.log(`Locale placeholder and untranslated-prose audit passed for ${localeNames.length} translation(s).`)
