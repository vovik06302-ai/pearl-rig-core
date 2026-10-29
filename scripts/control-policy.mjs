export function validateProfile(gpus) {
  if (!Array.isArray(gpus) || gpus.length !== 2) throw new Error('Нужен профиль обеих карт');
  return gpus.map((gpu, id) => {
    if (gpu.id !== id) throw new Error('Неверный порядок GPU');
    if (!Number.isInteger(gpu.pl) || gpu.pl < 100 || gpu.pl > 150) throw new Error('Лимит мощности: 100–150 Вт');
    if (!Number.isInteger(gpu.cclk) || gpu.cclk < 1800 || gpu.cclk > 2400) throw new Error('Частота ядра: 1800–2400 МГц');
    if (gpu.mclk !== 810 || gpu.kernel !== 2) throw new Error('Сохраните память 810 МГц и kernel 2');
    const fan = String(gpu.fan);
    if (fan !== '*' && (!/^\d{2,3}$/.test(fan) || +fan < 45 || +fan > 100)) throw new Error('Вентилятор: авто или 45–100%');
    return { id, cclk: gpu.cclk, mclk: 810, pl: gpu.pl, kernel: 2, fan };
  });
}

export function authorizeRequest(req, token) {
  if (req.headers.host !== '127.0.0.1:3000') return false;
  if (req.method === 'POST') return req.headers.origin === 'http://127.0.0.1:3000'
    && req.headers['content-type']?.split(';')[0] === 'application/json'
    && req.headers['x-pearl-token'] === token;
  return req.headers.origin === undefined || req.headers.origin === 'http://127.0.0.1:3000';
}
