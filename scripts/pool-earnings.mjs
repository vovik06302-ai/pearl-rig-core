const finite = value => typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : null;

export function normalizeEarnings(balance, stats, rewards, payouts, now = Date.now()) {
  // Pool reward-chart timestamps are UTC day buckets, in milliseconds.
  const utcDay = Math.floor(now / 86400000) * 86400000;
  const currentDay = Array.isArray(rewards) ? rewards.find(p => (p.timestamp ?? p.date) === utcDay) : null;
  return {
    currency: 'PRL', fetchedAt: new Date(now).toISOString(),
    confirmed: finite(balance?.confirmed), unconfirmed: finite(balance?.unconfirmed),
    balance: finite(balance?.total), payoutThreshold: finite(balance?.threshold),
    paid: finite(stats?.paid), week: finite(stats?.reward?.week), month: finite(stats?.reward?.month),
    todayUtc: finite(currentDay?.reward),
    payouts: Array.isArray(payouts?.results) ? payouts.results.slice(0, 5).map(p => ({
      amount: finite(p.received), date: p.date, txid: p.txid, status: p.status,
    })) : null,
  };
}

export async function fetchEarnings(wallet, fetcher = fetch) {
  if (!/^prl1p[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{58}$/.test(wallet)) throw new Error('Полный адрес выплат не настроен');
  const base = 'https://pool.kryptex.com/prl/api/v1/miner/';
  const endpoints = [`balance/${wallet}`, `payouts/${wallet}/stats`, `reward-chart/${wallet}`, `payouts/${wallet}`];
  const results = await Promise.allSettled(endpoints.map(async endpoint => {
    const response = await fetcher(base + endpoint, { signal: AbortSignal.timeout(12000), headers: { Accept: 'application/json' } });
    if (!response.ok) throw new Error(`Kryptex HTTP ${response.status}`);
    return response.json();
  }));
  if (results.every(r => r.status === 'rejected')) throw new Error('Kryptex недоступен. Новые начисления не получены.');
  const [balance, stats, rewards, payouts] = results.map(r => r.status === 'fulfilled' ? r.value : null);
  return { ...normalizeEarnings(balance, stats, rewards, payouts), wallet, source: 'Kryptex Pool API', partial: results.some(r => r.status === 'rejected') };
}
