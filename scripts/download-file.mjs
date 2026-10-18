import fs from 'node:fs'
import https from 'node:https'

const [url, out] = process.argv.slice(2)
if (!url || !out) {
  console.error('usage: node download-file.mjs <url> <out>')
  process.exit(1)
}

function get(url, redirects = 0) {
  if (redirects > 8) throw new Error('too many redirects')
  return new Promise((resolve, reject) => {
    const req = https.get(
      url,
      {
        headers: {
          'User-Agent': 'MurmurBuild',
          Accept: '*/*',
        },
      },
      (res) => {
        if (res.statusCode >= 300 && res.statusCode < 400 && res.headers.location) {
          res.resume()
          resolve(get(new URL(res.headers.location, url).toString(), redirects + 1))
          return
        }
        if (res.statusCode !== 200) {
          res.resume()
          reject(new Error(`HTTP ${res.statusCode} for ${url}`))
          return
        }
        resolve(res)
      },
    )
    req.on('error', reject)
    req.setTimeout(30000, () => req.destroy(new Error('timeout')))
  })
}

const res = await get(url)
const total = Number(res.headers['content-length'] || 0)
let received = 0
const file = fs.createWriteStream(out)
res.on('data', (chunk) => {
  received += chunk.length
  if (total > 0 && received % (1024 * 1024 * 10) < chunk.length) {
    process.stderr.write(
      `\r${(received / 1024 / 1024).toFixed(1)} / ${(total / 1024 / 1024).toFixed(1)} MB`,
    )
  }
})
res.pipe(file)
await new Promise((resolve, reject) => {
  file.on('finish', resolve)
  file.on('error', reject)
  res.on('error', reject)
})
console.error('')
console.log(`downloaded ${received} bytes to ${out}`)
