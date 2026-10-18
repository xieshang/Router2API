<script setup lang="ts">
import { computed, ref } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { NAlert, NCard, NSelect, NSpin, NStatistic } from 'naive-ui'
import VChart from 'vue-echarts'
import { use } from 'echarts/core'
import { CanvasRenderer } from 'echarts/renderers'
import { BarChart, LineChart } from 'echarts/charts'
import type { BarSeriesOption, LineSeriesOption } from 'echarts/charts'
import { GridComponent, LegendComponent, TooltipComponent } from 'echarts/components'
import type { EChartsOption } from 'echarts'
import { api } from '@/services/api'
import type { AnalyticsBucket } from '@/services/api'

use([CanvasRenderer, BarChart, LineChart, GridComponent, LegendComponent, TooltipComponent])
const range = ref('today')
const ranges = [{ label: '今天', value: 'today' }, { label: '昨天', value: 'yesterday' }, { label: '最近 7 天', value: '7d' }, { label: '最近 30 天', value: '30d' }]
const bounds = computed(() => { const now = new Date(); const end = new Date(now); const start = new Date(now); start.setHours(0, 0, 0, 0); if (range.value === 'yesterday') { start.setDate(start.getDate() - 1); end.setTime(start.getTime()); end.setDate(end.getDate() + 1) } else if (range.value === '7d') start.setDate(start.getDate() - 6); else if (range.value === '30d') start.setDate(start.getDate() - 29); return { fromUtc: start, toUtc: end } })
const query = useQuery({ queryKey: computed(() => ['analytics-overview', range.value, bounds.value.fromUtc.toISOString(), bounds.value.toUtc.toISOString()]), queryFn: () => api.analyticsOverview(bounds.value), refetchInterval: 30_000 })
const report = computed(() => query.data.value?.range)
const liveConnections = computed(() => Number((query.data.value?.live as { activeConnections?: number } | undefined)?.activeConnections ?? 0))

// 按模型堆叠成功次数：Top N 用固定调色板，其余汇入「其他」；失败段堆在顶部，使柱高=请求数；总请求数以蓝色折线叠加。
const TOP_MODEL_COUNT = 8
const OTHER_MODEL_KEY = '\u0000other'
const OTHER_LABEL = '其他'
const FAILURE_SERIES = '失败'
const UNKNOWN_MODEL_LABEL = '未标注模型'
const MODEL_COLORS = ['#2563EB', '#0891B2', '#7C3AED', '#DB2777', '#CA8A04', '#16A34A', '#EA580C', '#4F46E5']
const OTHER_COLOR = '#94A3B8'
const FAILURE_COLOR = '#EF4444'
const TOTAL_LINE_COLOR = '#1D4ED8'
const TREND_STACK = 'trend'
const RESERVED_LEGEND_LABELS = new Set([FAILURE_SERIES, OTHER_LABEL, '请求数'])

type ModelSeries = { key: string; label: string; total: number; color: string }

const bucketList = computed<AnalyticsBucket[]>(() => report.value?.buckets ?? [])
const bucketLabels = computed(() => bucketList.value.map(bucket => formatBucketLabel(bucket.startUtc)))

function formatBucketLabel(startUtc: string): string {
  const date = new Date(startUtc)
  if (Number.isNaN(date.getTime())) return String(startUtc ?? '')
  const month = String(date.getMonth() + 1).padStart(2, '0')
  const day = String(date.getDate()).padStart(2, '0')
  const hour = String(date.getHours()).padStart(2, '0')
  const minute = String(date.getMinutes()).padStart(2, '0')
  return `${month}-${day} ${hour}:${minute}`
}

function toCount(value: unknown): number {
  const count = typeof value === 'number' ? value : Number(value)
  return Number.isFinite(count) && count > 0 ? Math.round(count) : 0
}

function modelEntryCounts(bucket: AnalyticsBucket | undefined): Array<[string, number]> {
  const raw = bucket?.successesByModel
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return []
  return Object.entries(raw).filter(([, value]) => toCount(value) > 0).map(([key, value]) => [key, toCount(value)])
}

function assignModelLabel(base: string, used: Set<string>): string {
  const preferred = RESERVED_LEGEND_LABELS.has(base) ? `${base}（模型）` : base
  let label = preferred
  let index = 2
  while (used.has(label)) { label = `${preferred}（${index}）`; index += 1 }
  used.add(label)
  return label
}

// 整个范围内按模型成功总数排序：Top 8 固定，其余合并为「其他」。字段缺失时返回空数组，图表回退旧样式。
const modelSeries = computed<ModelSeries[]>(() => {
  const totals = new Map<string, number>()
  for (const bucket of bucketList.value)
    for (const [key, count] of modelEntryCounts(bucket)) totals.set(key, (totals.get(key) ?? 0) + count)
  const ranked = [...totals.entries()].sort((left, right) => right[1] - left[1] || (left[0] < right[0] ? -1 : left[0] > right[0] ? 1 : 0))
  const used = new Set<string>()
  const entries = ranked.slice(0, TOP_MODEL_COUNT).map(([key, total], index) => ({ key, label: assignModelLabel(key === '' ? UNKNOWN_MODEL_LABEL : key, used), total, color: MODEL_COLORS[index] ?? OTHER_COLOR }))
  const otherTotal = ranked.slice(TOP_MODEL_COUNT).reduce((sum, [, count]) => sum + count, 0)
  if (otherTotal > 0) {
    // Top 模型里的「其他」已被重命名为「其他（模型）」，这里的合并项优先占用「其他」。
    let otherLabel = OTHER_LABEL
    let index = 2
    while (used.has(otherLabel)) { otherLabel = `${OTHER_LABEL}（${index}）`; index += 1 }
    used.add(otherLabel)
    entries.push({ key: OTHER_MODEL_KEY, label: otherLabel, total: otherTotal, color: OTHER_COLOR })
  }
  return entries
})
const hasModelData = computed(() => modelSeries.value.length > 0)
const topModelKeys = computed(() => new Set(modelSeries.value.filter(entry => entry.key !== OTHER_MODEL_KEY).map(entry => entry.key)))

function modelCount(bucket: AnalyticsBucket, entry: ModelSeries, topKeys: Set<string>): number {
  if (entry.key === OTHER_MODEL_KEY) return modelEntryCounts(bucket).filter(([key]) => !topKeys.has(key)).reduce((sum, [, count]) => sum + count, 0)
  const counts = new Map(modelEntryCounts(bucket))
  return counts.get(entry.key) ?? 0
}

function shortenLegend(value: string): string { return value.length > 18 ? `${value.slice(0, 17)}…` : value }

const HTML_ESCAPES: Record<string, string> = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }
function escapeHtml(value: string): string { return value.replace(/[&<>"']/g, character => HTML_ESCAPES[character] ?? character) }

type TrendTooltipParam = { seriesName?: string; value?: unknown; dataIndex?: number; marker?: string }
function trendTooltip(params: unknown): string {
  const list = (Array.isArray(params) ? params : [params]) as TrendTooltipParam[]
  if (list.length === 0) return ''
  const dataIndex = list[0]?.dataIndex ?? 0
  const bucket = bucketList.value[dataIndex]
  const title = escapeHtml(bucketLabels.value[dataIndex] ?? '')
  const rows = list.filter(item => toCount(item.value) > 0).map(item => `<div style="display:flex;align-items:center;gap:6px">${item.marker ?? ''}<span style="flex:1">${escapeHtml(item.seriesName ?? '')}</span><b>${toCount(item.value)}</b></div>`)
  const footer = bucket ? `<div style="margin-top:6px;padding-top:4px;border-top:1px solid rgba(148,163,184,.45)">请求 ${bucket.requests} · 成功 ${bucket.successes} · 失败 ${bucket.failures}</div>` : ''
  return `<div style="font-weight:600;margin-bottom:4px">${title}</div>${rows.join('') || '<div>无请求</div>'}${footer}`
}

const option = computed<EChartsOption>(() => {
  const buckets = bucketList.value
  const labelInterval: number | 'auto' = buckets.length > 240 ? Math.max(0, Math.ceil(buckets.length / 120) - 1) : 'auto'
  const series: Array<BarSeriesOption | LineSeriesOption> = []
  if (hasModelData.value) {
    const topKeys = topModelKeys.value
    for (const entry of modelSeries.value)
      series.push({ name: entry.label, type: 'bar', stack: TREND_STACK, barMaxWidth: 18, itemStyle: { color: entry.color }, emphasis: { focus: 'series' }, data: buckets.map(bucket => modelCount(bucket, entry, topKeys)) })
    series.push({ name: FAILURE_SERIES, type: 'bar', stack: TREND_STACK, barMaxWidth: 18, itemStyle: { color: FAILURE_COLOR }, emphasis: { focus: 'series' }, data: buckets.map(bucket => bucket.failures) })
    // 叠加原来的总请求数折线：柱内为成功/失败构成，折线为总量走势。
    series.push({ name: '请求数', type: 'line', smooth: true, symbol: 'circle', symbolSize: 4, showSymbol: buckets.length <= 96, data: buckets.map(bucket => bucket.requests), lineStyle: { color: TOTAL_LINE_COLOR, width: 2 }, itemStyle: { color: TOTAL_LINE_COLOR } })
  } else {
    series.push({ name: '请求数', type: 'line', smooth: true, data: buckets.map(bucket => bucket.requests), itemStyle: { color: '#2563EB' }, areaStyle: { color: '#DBEAFE' } })
    series.push({ name: '失败数', type: 'bar', data: buckets.map(bucket => bucket.failures), itemStyle: { color: '#FCA5A5' } })
  }
  const legendData = hasModelData.value ? [...modelSeries.value.map(entry => entry.label), FAILURE_SERIES, '请求数'] : ['请求数', '失败数']
  return {
    tooltip: { trigger: 'axis', axisPointer: { type: hasModelData.value ? 'shadow' : 'line' }, formatter: trendTooltip },
    legend: { type: 'scroll' as const, top: 0, left: 0, right: 0, itemWidth: 12, itemHeight: 8, data: legendData, formatter: shortenLegend },
    grid: { left: 12, right: 24, top: 42, bottom: 52, containLabel: true },
    xAxis: { type: 'category', axisLabel: { hideOverlap: true, margin: 12, interval: labelInterval, formatter: (value: string) => value.replace(' ', '\n') }, data: bucketLabels.value },
    yAxis: { type: 'value', minInterval: 1 },
    series,
  }
})

const modelSummary = computed(() => {
  if (!hasModelData.value) return ''
  const total = modelSeries.value.reduce((sum, entry) => sum + entry.total, 0)
  return `模型维度：共 ${total} 次成功，按模型成功次数排序：${modelSeries.value.map(entry => `${entry.label} ${entry.total} 次`).join('，')}。`
})
const trendSummary = computed(() => {
  const buckets = bucketList.value
  if (buckets.length === 0) return '暂无请求趋势数据。'
  const limitPerBucketDetail = 168
  const rows = buckets.map((bucket, index) => {
    const base = `${bucketLabels.value[index]} ${bucket.requests} 次请求，${bucket.failures} 次失败`
    if (!hasModelData.value || buckets.length > limitPerBucketDetail) return base
    const topKeys = topModelKeys.value
    const modelDetail = modelSeries.value.map(entry => { const count = modelCount(bucket, entry, topKeys); return count > 0 ? `${entry.label} ${count} 次` : '' }).filter(text => text.length > 0).join('、')
    return modelDetail.length > 0 ? `${base}，成功按模型：${modelDetail}` : base
  })
  const note = hasModelData.value && buckets.length > limitPerBucketDetail ? '当前范围桶数较多，逐桶仅列出请求与失败次数，模型维度见上述汇总。' : ''
  return `${modelSummary.value}请求趋势数据：${rows.join('；')}${note}`
})
</script>
<template><NAlert v-if="query.isError.value" type="error" title="分析数据加载失败" class="mb-20">请稍后重试。</NAlert><NSpin v-if="query.isPending.value" size="large" class="center-spin" /><template v-else><div class="analytics-summary-grid mb-20"><NCard><NStatistic label="范围请求" :value="report?.requests ?? 0" /></NCard><NCard><NStatistic label="失败请求" :value="report?.failures ?? 0" /></NCard><NCard><NStatistic label="平均延迟" :value="`${Math.round(report?.averageLatencyMs ?? 0)} ms`" /></NCard><NCard><NStatistic label="P95 延迟" :value="`${Math.round(report?.p95LatencyMs ?? 0)} ms`" /></NCard><NCard><NStatistic label="实时连接" :value="liveConnections" /></NCard></div><NCard title="请求趋势" class="dashboard-card"><template #header-extra><NSelect v-model:value="range" :options="ranges" style="width: 150px" /></template><div class="chart-wrap large"><VChart :option="option" autoresize /></div><p class="sr-only">{{ trendSummary }}</p></NCard></template></template>
