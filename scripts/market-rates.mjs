const finite = value => typeof value === 'number' && Number.isFinite(value) && value > 0 ? value : null;

function parseCbrUsdRub(xml) {
  const block = xml.match(/<Valute[^>]*>[\s\S]*?<CharCode>USD<\/CharCode>[\s\S]*?<\/Valute>/i)?.[0];
  const value = block?.match(/<Value>([^<]+)<\/Value>/i)?.[1]?.replace(',', '.');
  return finite(Number(value));
}

export function normalizeMarketRates(priceChart, cbrXml, now = Date.now()) {
  const points = Array.isArray(priceChart) ? priceChart
    .map(point => ({ timestamp: Number(point?.timestamp), price: finite(Number(point?.price)) }))
    .filter(point => point.timestamp > 0 && point.price !== null && point.timestamp <= now)
    .sort((a, b) => a.timestamp - b.timestamp) : [];
  const latest = points.at(-1) ?? null;
  const usdRub = parseCbrUsdRub(cbrXml ?? '');
  const prlUsd = latest?.price ?? null;
  return {
    currency: 'RUB',
    prlUsd,
    usdRub,
    prlRub: prlUsd !== null && usdRub !== null ? prlUsd * usdRub : null,
    priceAt: latest ? new Date(latest.timestamp).toISOString() : null,
    fetchedAt: new Date(now).toISOString(),
    sources: {
      prlUsd: 'https://pool.kryptex.com/api/v1/coin/prl/price/chart',
      usdRub: 'https://www.cbr.ru/scripts/XML_daily.asp',
    },
  };
}

export async function fetchMarketRates(fetcher = fetch, now = Date.now()) {
  const [price, cbr] = await Promise.all([
    fetcher('https://pool.kryptex.com/api/v1/coin/prl/price/chart', { signal: AbortSignal.timeout(12000), headers: { Accept: 'application/json' } }),
    fetcher('https://www.cbr.ru/scripts/XML_daily.asp', { signal: AbortSignal.timeout(12000), headers: { Accept: 'application/xml, text/xml' } }),
  ]);
  if (!price.ok) throw new Error(`Kryptex price HTTP ${price.status}`);
  if (!cbr.ok) throw new Error(`Банк России HTTP ${cbr.status}`);
  const rates = normalizeMarketRates(await price.json(), await cbr.text(), now);
  if (rates.prlRub === null) throw new Error('Официальный курс PRL/RUB пока недоступен');
  return { ...rates, source: 'Kryptex Pool + Банк России' };
}
