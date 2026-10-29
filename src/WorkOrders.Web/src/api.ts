export interface Operation {
  operationSequence: number; workCenter: string
  scheduledStartDate: string; scheduledEndDate: string
  actualStartDate: string | null; actualEndDate: string | null
  actualResourceHrs: number | null; plannedCost: number; actualCost: number | null
}
export interface WorkOrder {
  id: string; legacyWorkOrderId: number | null; productId: number; productName: string
  orderQty: number; scrappedQty: number; stockedQty: number
  startDate: string; dueDate: string; endDate: string | null; status: string
  operations: Operation[]
}

async function get<T>(url: string): Promise<T> {
  const r = await fetch(url)
  if (!r.ok) throw new Error(`${url}: ${r.status}`)
  return r.json()
}

export const listWorkOrders = (status: string) => get<WorkOrder[]>(`/api/work-orders?take=200&status=${encodeURIComponent(status)}`)
export const getWorkOrder = (id: string) => get<WorkOrder>(`/api/work-orders/${id}`)

export interface Planner { businessEntityId: number; name: string }
export interface Product { id: number; name: string }
export interface ScrapReason { id: number; name: string }

export const listPlanners = () => get<Planner[]>('/api/planners')
export const listProducts = () => get<Product[]>('/api/products')
export const listScrapReasons = () => get<ScrapReason[]>('/api/scrap-reasons')

/** Sends a write as the chosen planner (dev auth stub). Resolves to an error message, or null on success. */
export async function send(method: 'POST' | 'PUT', url: string, planner: number, body: unknown): Promise<string | null> {
  const r = await fetch(url, {
    method, body: JSON.stringify(body),
    headers: { 'content-type': 'application/json', 'X-Planner': String(planner) },
  })
  if (r.ok) return null
  try { return (await r.json()).error ?? `${r.status}` } catch { return `${r.status}` }
}
