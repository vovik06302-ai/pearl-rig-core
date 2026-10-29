import React, { useId } from 'react';

export interface SpeedometerGaugeProps {
  label: string;
  sublabel?: string;
  value: number | null;
  min: number;
  max: number;
  unit: string;
  warningThreshold?: number;
  criticalThreshold?: number;
  decimals?: number;
  size?: 'sm' | 'md' | 'lg';
  accent?: 'emerald' | 'cyan' | 'amber' | 'violet' | 'fire';
  extraInfo?: Array<{ label: string; value: string; highlight?: 'ok' | 'warn' | 'err' }>;
}

function polarToCartesian(cx: number, cy: number, r: number, angleInDegrees: number) {
  const angleInRadians = ((angleInDegrees - 90) * Math.PI) / 180.0;
  return {
    x: cx + r * Math.cos(angleInRadians),
    y: cy + r * Math.sin(angleInRadians),
  };
}

function describeArc(cx: number, cy: number, r: number, startAngle: number, endAngle: number) {
  if (Math.abs(endAngle - startAngle) < 0.1) return '';
  const start = polarToCartesian(cx, cy, r, endAngle);
  const end = polarToCartesian(cx, cy, r, startAngle);
  const largeArcFlag = endAngle - startAngle <= 180 ? '0' : '1';
  return ['M', start.x, start.y, 'A', r, r, 0, largeArcFlag, 0, end.x, end.y].join(' ');
}

export const SpeedometerGauge: React.FC<SpeedometerGaugeProps> = ({
  label,
  sublabel,
  value,
  min,
  max,
  unit,
  warningThreshold,
  criticalThreshold,
  decimals = 1,
  size = 'md',
  accent = 'emerald',
  extraInfo,
}) => {
  const uid = useId().replace(/:/g, '');
  const hasData = value !== null && Number.isFinite(value);
  const numericValue = hasData ? (value as number) : min;
  const safeRange = Math.max(0.0001, max - min);
  const clamped = Math.min(Math.max(numericValue, min), max);
  const ratio = hasData ? (clamped - min) / safeRange : 0;

  const startAngle = -130;
  const endAngle = 130;
  const totalSweep = endAngle - startAngle;
  const currentAngle = startAngle + ratio * totalSweep;

  const warnRatio =
    warningThreshold !== undefined
      ? Math.min(Math.max((warningThreshold - min) / safeRange, 0), 1)
      : 1;
  const critRatio =
    criticalThreshold !== undefined
      ? Math.min(Math.max((criticalThreshold - min) / safeRange, 0), 1)
      : 1;

  const warnAngle = startAngle + warnRatio * totalSweep;
  const critAngle = startAngle + critRatio * totalSweep;

  const isCritical = hasData && criticalThreshold !== undefined && numericValue >= criticalThreshold;
  const isWarning =
    hasData && !isCritical && warningThreshold !== undefined && numericValue >= warningThreshold;

  const palette = {
    emerald: { main: '#10B981', bright: '#34D399', glow: 'rgba(16,185,129,0.35)' },
    cyan: { main: '#06B6D4', bright: '#22D3EE', glow: 'rgba(6,182,212,0.35)' },
    amber: { main: '#F59E0B', bright: '#FBBF24', glow: 'rgba(245,158,11,0.35)' },
    violet: { main: '#8B5CF6', bright: '#A78BFA', glow: 'rgba(139,92,246,0.35)' },
    fire: { main: '#F97316', bright: '#FB923C', glow: 'rgba(249,115,22,0.4)' },
  }[accent];

  const statusColor = !hasData
    ? '#64748B'
    : isCritical
    ? '#EF4444'
    : isWarning
    ? '#F59E0B'
    : palette.bright;

  const cx = 105;
  const cy = 104;
  const radius = 76;

  const majorSteps = 5;
  const minorSteps = 30;

  const ticks = [];
  for (let i = 0; i <= minorSteps; i++) {
    const tRatio = i / minorSteps;
    const angle = startAngle + tRatio * totalSweep;
    const isMajor = i % (minorSteps / majorSteps) === 0;
    const isPassed = hasData && angle <= currentAngle && numericValue > min;
    const outerPt = polarToCartesian(cx, cy, radius - 6, angle);
    const innerPt = polarToCartesian(cx, cy, radius - (isMajor ? 16 : 11), angle);
    const labelPt = polarToCartesian(cx, cy, radius - 27, angle);
    const tickVal = Math.round(min + tRatio * safeRange);

    ticks.push(
      <g key={i}>
        <line
          x1={outerPt.x}
          y1={outerPt.y}
          x2={innerPt.x}
          y2={innerPt.y}
          stroke={isPassed ? statusColor : isMajor ? '#94A3B8' : '#334155'}
          strokeWidth={isMajor ? 2 : 1.1}
          strokeLinecap="round"
        />
        {isMajor && (
          <text
            x={labelPt.x}
            y={labelPt.y + 3}
            textAnchor="middle"
            fill={isPassed ? '#E2E8F0' : '#64748B'}
            fontSize="8.5"
            fontFamily="JetBrains Mono, monospace"
            fontWeight="600"
          >
            {tickVal}
          </text>
        )}
      </g>
    );
  }

  const needleTip = polarToCartesian(cx, cy, radius - 11, currentAngle);
  const needleTail = polarToCartesian(cx, cy, 15, currentAngle + 180);
  const needleSideL = polarToCartesian(cx, cy, 4.2, currentAngle - 90);
  const needleSideR = polarToCartesian(cx, cy, 4.2, currentAngle + 90);

  const svgHeight = size === 'sm' ? 'h-36' : size === 'lg' ? 'h-48' : 'h-44';

  return (
    <div className="relative p-4 rounded-2xl border border-white/[0.08] bg-gradient-to-b from-[#141E30]/95 via-[#0F1726]/95 to-[#0A0F1A]/95 shadow-[0_12px_32px_-8px_rgba(0,0,0,0.65)] flex flex-col justify-between overflow-hidden group hover:border-white/[0.15] transition-all">
      <div
        className="pointer-events-none absolute -top-12 left-1/2 -translate-x-1/2 w-40 h-20 rounded-full blur-2xl opacity-25 transition-opacity group-hover:opacity-45"
        style={{ background: statusColor }}
      />

      <div className="relative flex items-start justify-between gap-2">
        <div>
          <div className="text-xs font-bold text-slate-100 tracking-tight">{label}</div>
          {sublabel && <div className="text-[11px] text-slate-400 font-mono mt-0.5">{sublabel}</div>}
        </div>
        <span
          className={`px-2 py-0.5 rounded-md text-[10px] font-mono font-bold uppercase tracking-wider ${
            !hasData
              ? 'bg-slate-800/80 text-slate-400 border border-slate-700'
              : isCritical
              ? 'bg-red-500/20 text-red-400 border border-red-500/40'
              : isWarning
              ? 'bg-amber-500/20 text-amber-300 border border-amber-500/40'
              : 'bg-emerald-500/15 text-emerald-400 border border-emerald-500/30'
          }`}
        >
          {!hasData ? 'НЕТ ДАННЫХ' : isCritical ? 'ПЕРЕГРЕВ / АВАРИЯ' : isWarning ? 'ВНИМАНИЕ' : 'НОРМА'}
        </span>
      </div>

      <div className="relative flex flex-col items-center justify-center my-0.5">
        <svg viewBox="0 0 210 158" className={`w-full ${svgHeight} overflow-visible`}>
          <defs>
            <radialGradient id={`dialBg-${uid}`} cx="50%" cy="55%" r="50%">
              <stop offset="0%" stopColor="#1E293B" stopOpacity="0.45" />
              <stop offset="75%" stopColor="#0F172A" stopOpacity="0.2" />
              <stop offset="100%" stopColor="#090D16" stopOpacity="0" />
            </radialGradient>
            <linearGradient id={`needleGrad-${uid}`} x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stopColor="#FFFFFF" />
              <stop offset="45%" stopColor={statusColor} />
              <stop offset="100%" stopColor="#0F172A" />
            </linearGradient>
            <filter id={`glow-${uid}`} x="-25%" y="-25%" width="150%" height="150%">
              <feGaussianBlur stdDeviation="3.2" result="blur" />
              <feComposite in="SourceGraphic" in2="blur" operator="over" />
            </filter>
          </defs>

          <circle cx={cx} cy={cy} r={radius + 2} fill={`url(#dialBg-${uid})`} />

          <path
            d={describeArc(cx, cy, radius + 6, startAngle, endAngle)}
            fill="none"
            stroke="#1E293B"
            strokeWidth="1.5"
            strokeDasharray="3 3"
          />

          <path
            d={describeArc(cx, cy, radius, startAngle, endAngle)}
            fill="none"
            stroke="#0F172A"
            strokeWidth="9"
            strokeLinecap="round"
          />

          <path
            d={describeArc(cx, cy, radius, startAngle, warnAngle)}
            fill="none"
            stroke={palette.main}
            strokeOpacity="0.2"
            strokeWidth="9"
          />
          <path
            d={describeArc(cx, cy, radius, warnAngle, critAngle)}
            fill="none"
            stroke="#F59E0B"
            strokeOpacity="0.24"
            strokeWidth="9"
          />
          <path
            d={describeArc(cx, cy, radius, critAngle, endAngle)}
            fill="none"
            stroke="#EF4444"
            strokeOpacity="0.28"
            strokeWidth="9"
          />

          {hasData && ratio > 0.005 && (
            <path
              d={describeArc(cx, cy, radius, startAngle, currentAngle)}
              fill="none"
              stroke={statusColor}
              strokeWidth="9"
              strokeLinecap="round"
              filter={`url(#glow-${uid})`}
              className="transition-all duration-500 ease-out"
            />
          )}

          {ticks}

          <polygon
            points={`${needleTip.x},${needleTip.y + 2} ${needleSideL.x},${needleSideL.y + 2} ${needleTail.x},${needleTail.y + 2} ${needleSideR.x},${needleSideR.y + 2}`}
            fill="rgba(0,0,0,0.55)"
            className="transition-all duration-500 ease-out"
          />

          <polygon
            points={`${needleTip.x},${needleTip.y} ${needleSideL.x},${needleSideL.y} ${needleTail.x},${needleTail.y} ${needleSideR.x},${needleSideR.y}`}
            fill={`url(#needleGrad-${uid})`}
            stroke={statusColor}
            strokeWidth="0.6"
            className="transition-all duration-500 ease-out"
          />

          <circle cx={cx} cy={cy} r="11" fill="#0B1120" stroke="#334155" strokeWidth="2" />
          <circle cx={cx} cy={cy} r="4.5" fill={statusColor} />
          <circle cx={cx - 1.2} cy={cy - 1.2} r="1.5" fill="#FFFFFF" fillOpacity="0.8" />

          <rect
            x={cx - 52}
            y={cy + 16}
            width="104"
            height="36"
            rx="8"
            fill="#090D16"
            fillOpacity="0.85"
            stroke="#1E293B"
            strokeWidth="1"
          />
          <text
            x={cx}
            y={cy + 34}
            textAnchor="middle"
            fill={hasData ? '#F8FAFC' : '#94A3B8'}
            fontSize={hasData ? '15.5' : '11.5'}
            fontWeight="700"
            fontFamily="JetBrains Mono, monospace"
          >
            {hasData ? numericValue.toFixed(decimals) : 'нет данных'}
          </text>
          <text
            x={cx}
            y={cy + 47}
            textAnchor="middle"
            fill={statusColor}
            fontSize="8.5"
            fontWeight="700"
            fontFamily="JetBrains Mono, monospace"
          >
            {unit}
          </text>
        </svg>
      </div>

      {extraInfo && extraInfo.length > 0 && (
        <div className="relative pt-2.5 border-t border-white/[0.06] grid grid-cols-2 gap-x-3 gap-y-1.5 text-[11px]">
          {extraInfo.map((item, idx) => (
            <div
              key={idx}
              className="flex items-center justify-between gap-1 bg-slate-950/40 px-2 py-1 rounded-md border border-white/[0.04]"
            >
              <span className="text-slate-400 truncate">{item.label}</span>
              <span
                className={`font-mono font-semibold whitespace-nowrap ${
                  item.highlight === 'err'
                    ? 'text-red-400'
                    : item.highlight === 'warn'
                    ? 'text-amber-300'
                    : item.highlight === 'ok'
                    ? 'text-emerald-400'
                    : 'text-slate-200'
                }`}
              >
                {item.value}
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
};
