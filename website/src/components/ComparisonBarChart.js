import styles from './ComparisonBarChart.module.css';

const integerFormatter = new Intl.NumberFormat('en-US', {
  maximumFractionDigits: 0,
});

function formatRatio(value) {
  const precision = value > 0 && value < 0.01 ? 3 : 2;
  return `${value.toFixed(precision)}×`;
}

function formatDuration(value) {
  if (value >= 1_000) {
    return `${(value / 1_000).toLocaleString('en-US', {maximumFractionDigits: 1})} μs`;
  }

  return `${value.toLocaleString('en-US', {maximumFractionDigits: 1})} ns`;
}

function formatBytes(value) {
  if (value >= 1_024) {
    return `${(value / 1_024).toLocaleString('en-US', {maximumFractionDigits: 2})} KB`;
  }

  return `${integerFormatter.format(value)} B`;
}

function formatValue(value, format) {
  switch (format) {
    case 'bytes':
      return formatBytes(value);
    case 'duration-ns':
      return formatDuration(value);
    case 'integer':
      return integerFormatter.format(value);
    case 'ratio':
      return formatRatio(value);
    default:
      return String(value);
  }
}

function Bar({barClassName, label, maxValue, relativeTo, value, valueFormat}) {
  const width = maxValue > 0 ? Math.min((value / maxValue) * 100, 100) : 0;
  const formattedValue = formatValue(value, valueFormat);
  const ratio = relativeTo > 0 ? ` (${formatRatio(value / relativeTo)})` : '';
  const displayedValue = `${formattedValue}${ratio}`;
  const emptyClassName = value === 0 ? styles.empty : '';

  return (
    <div className={styles.series} aria-label={`${label}: ${displayedValue}`}>
      <span className={styles.seriesLabel}>{label}</span>
      <span className={styles.track} aria-hidden="true">
        <span
          className={`${styles.bar} ${barClassName} ${emptyClassName}`}
          style={{width: `${width}%`}}
        />
      </span>
      <span className={styles.value}>{displayedValue}</span>
    </div>
  );
}

function rowValues({other, respire, respireServer}) {
  return respireServer === undefined
    ? [other, respire]
    : [other, respireServer, respire];
}

export default function ComparisonBarChart({
  data,
  description,
  format = 'integer',
  otherLabel = 'StackExchange.Redis',
  respireLabel = 'Respire',
  respireServerLabel = 'Respire server read',
  scale = 'chart',
  showRatio = false,
  title,
}) {
  const chartMaxValue = Math.max(
    0,
    ...data.flatMap(rowValues),
  );

  return (
    <figure className={`${styles.chart} ${showRatio ? styles.withRatios : ''}`}>
      <figcaption>
        <strong className={styles.title}>{title}</strong>
        {description && <span className={styles.description}>{description}</span>}
      </figcaption>
      <div className={styles.plot}>
        {data.map((row) => {
          const {label, other, respire, respireServer} = row;
          const maxValue = scale === 'group'
            ? Math.max(...rowValues(row))
            : chartMaxValue;

          return (
            <div className={styles.group} key={label}>
              <div className={styles.groupLabel}>{label}</div>
              <Bar
                barClassName={styles.other}
                label={otherLabel}
                maxValue={maxValue}
                relativeTo={showRatio ? other : undefined}
                value={other}
                valueFormat={format}
              />
              {respireServer !== undefined && (
                <Bar
                  barClassName={styles.respireServer}
                  label={respireServerLabel}
                  maxValue={maxValue}
                  relativeTo={showRatio ? other : undefined}
                  value={respireServer}
                  valueFormat={format}
                />
              )}
              <Bar
                barClassName={styles.respire}
                label={respireLabel}
                maxValue={maxValue}
                relativeTo={showRatio ? other : undefined}
                value={respire}
                valueFormat={format}
              />
            </div>
          );
        })}
      </div>
    </figure>
  );
}
