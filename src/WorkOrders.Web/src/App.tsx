import { useEffect, useState } from 'react'
import {
  getWorkOrder, listPlanners, listProducts, listScrapReasons, listWorkOrders, send,
  type Planner, type Product, type ScrapReason, type WorkOrder,
} from './api'

const STATUSES = ['All', 'Created', 'Released', 'In progress', 'Closed']
const day = (d: string | null) => (d ? d.slice(0, 10) : '')
const stamp = (d: string | null) => (d ? d.slice(0, 16).replace('T', ' ') : '')
function usePlanner() {
  const [planners, setPlanners] = useState<Planner[]>([])
  const [id, setId] = useState<number>(() => Number(localStorage.getItem('planner')) || 0)
  useEffect(() => { listPlanners().then(p => { setPlanners(p); if (!id && p[0]) setId(p[0].businessEntityId) }) }, [id])
  useEffect(() => { if (id) localStorage.setItem('planner', String(id)) }, [id])
  return { planners, id, setId }
}

const Badge = ({ s }: { s: string }) => <span className={`badge ${s.split(' ')[0]}`}>{s}</span>

function List({ open, planner }: { open: (id: string) => void; planner: number }) {
  const [status, setStatus] = useState('All')
  const [rows, setRows] = useState<WorkOrder[] | null>(null)
  const [error, setError] = useState('')
  const [products, setProducts] = useState<Product[]>([])
  const [form, setForm] = useState({ productId: 0, orderQty: 10, dueDate: new Date(Date.now() + 14 * 864e5).toISOString().slice(0, 10) })
  const [tick, setTick] = useState(0)

  useEffect(() => { listProducts().then(p => { setProducts(p); setForm(f => ({ ...f, productId: f.productId || p[0]?.id || 0 })) }) }, [])
  useEffect(() => {
    listWorkOrders(status).then(setRows).catch(e => setError(String(e)))
  }, [status, tick])
  // Created orders are accepted at once; the legacy id arrives a moment later, so refresh shortly after.
  useEffect(() => { if (!rows?.some(w => w.legacyWorkOrderId === null)) return; const t = setTimeout(() => setTick(x => x + 1), 1500); return () => clearTimeout(t) }, [rows])

  async function create(e: React.FormEvent) {
    e.preventDefault()
    setError((await send('POST', '/api/work-orders', planner, form)) ?? '')
    setTick(x => x + 1)
  }

  return (
    <>
      <div className="bar">
        <label>Status <select value={status} onChange={e => setStatus(e.target.value)}>
          {STATUSES.map(s => <option key={s}>{s}</option>)}
        </select></label>
        <span className="muted">{rows ? `${rows.length} shown` : 'loading…'}</span>
      </div>
      <form className="bar" onSubmit={create}>
        <strong>New</strong>
        <select value={form.productId} onChange={e => setForm({ ...form, productId: Number(e.target.value) })}>
          {products.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
        </select>
        <input type="number" min={1} value={form.orderQty} style={{ width: 70 }}
          onChange={e => setForm({ ...form, orderQty: Number(e.target.value) })} />
        <input type="date" value={form.dueDate} onChange={e => setForm({ ...form, dueDate: e.target.value })} />
        <button disabled={!planner || !form.productId}>Create</button>
      </form>
      {error && <p role="alert">{error}</p>}
      <table>
        <thead><tr>
          <th>Order</th><th>Product</th><th className="num">Qty</th><th className="num">Scrap</th>
          <th className="hide-sm">Due</th><th>Status</th>
        </tr></thead>
        <tbody>
          {rows?.map(w => (
            <tr key={w.id} className="row" onClick={() => open(w.id)}>
              <td>{w.legacyWorkOrderId ?? <span className="muted">pending…</span>}</td><td>{w.productName}</td>
              <td className="num">{w.orderQty}</td><td className="num">{w.scrappedQty}</td>
              <td className="hide-sm">{day(w.dueDate)}</td><td><Badge s={w.status} /></td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  )
}

function Detail({ id, back, planner }: { id: string; back: () => void; planner: number }) {
  const [w, setW] = useState<WorkOrder | null>(null)
  const [reasons, setReasons] = useState<ScrapReason[]>([])
  const [plan, setPlan] = useState({ orderQty: '', dueDate: '' })
  const [scrap, setScrap] = useState({ quantity: 1, reasonId: 1 })
  const [message, setMessage] = useState('')
  const [tick, setTick] = useState(0)

  useEffect(() => { listScrapReasons().then(setReasons) }, [])
  useEffect(() => {
    getWorkOrder(id).then(x => { setW(x); setPlan(p => ({ orderQty: p.orderQty || String(x.orderQty), dueDate: p.dueDate || x.dueDate.slice(0, 10) })) })
  }, [id, tick])
  // The legacy side's confirmation replaces what we showed optimistically; pick it up shortly after an edit.
  useEffect(() => { if (!tick) return; const t = setTimeout(() => getWorkOrder(id).then(setW), 1500); return () => clearTimeout(t) }, [id, tick])
  if (!w) return <p>loading…</p>
  const editable = w.legacyWorkOrderId !== null && w.status !== 'Closed'

  async function savePlan(e: React.FormEvent) {
    e.preventDefault()
    setMessage((await send('PUT', `/api/work-orders/${id}/plan`, planner, { orderQty: Number(plan.orderQty), dueDate: plan.dueDate })) ?? 'Sent to the legacy system.')
    setTick(x => x + 1)
  }
  async function saveScrap(e: React.FormEvent) {
    e.preventDefault()
    setMessage((await send('POST', `/api/work-orders/${id}/scrap`, planner, scrap)) ?? 'Sent to the legacy system.')
    setTick(x => x + 1)
  }
  return (
    <>
      <button onClick={back}>← Back</button>
      <h1 style={{ marginTop: 12 }}>Work order {w.legacyWorkOrderId ?? '(pending)'}: {w.orderQty} × {w.productName} <Badge s={w.status} /></h1>
      <p className="muted">
        Start {day(w.startDate)} · due {day(w.dueDate)}{w.endDate && ` · closed ${day(w.endDate)}`} ·
        scrapped {w.scrappedQty}, stocked {w.stockedQty}
      </p>
      {w.legacyWorkOrderId === null && <p className="muted">Waiting for the legacy system to accept this order…</p>}
      {editable && (
        <div className="bar">
          <form className="bar" onSubmit={savePlan} style={{ margin: 0 }}>
            <strong>Plan</strong>
            <input type="number" min={1} value={plan.orderQty} style={{ width: 70 }} onChange={e => setPlan({ ...plan, orderQty: e.target.value })} />
            <input type="date" value={plan.dueDate} onChange={e => setPlan({ ...plan, dueDate: e.target.value })} />
            <button>Save</button>
          </form>
          <form className="bar" onSubmit={saveScrap} style={{ margin: 0 }}>
            <strong>Scrap</strong>
            <input type="number" min={1} value={scrap.quantity} style={{ width: 60 }} onChange={e => setScrap({ ...scrap, quantity: Number(e.target.value) })} />
            <select value={scrap.reasonId} onChange={e => setScrap({ ...scrap, reasonId: Number(e.target.value) })}>
              {reasons.map(r => <option key={r.id} value={r.id}>{r.name}</option>)}
            </select>
            <button>Add</button>
          </form>
        </div>
      )}
      {message && <p role="status" className="muted">{message}</p>}
      <table>
        <thead><tr>
          <th>Op</th><th>Work center</th><th className="hide-sm">Scheduled</th>
          <th>Actual start</th><th>Actual end</th><th className="num">Hours</th><th className="num">Cost</th>
        </tr></thead>
        <tbody>
          {w.operations.map(o => (
            <tr key={o.operationSequence}>
              <td>{o.operationSequence}</td><td>{o.workCenter}</td>
              <td className="hide-sm">{day(o.scheduledStartDate)} → {day(o.scheduledEndDate)}</td>
              <td>{stamp(o.actualStartDate)}</td><td>{stamp(o.actualEndDate)}</td>
              <td className="num">{o.actualResourceHrs ?? ''}</td>
              <td className="num">{o.actualCost?.toFixed(2) ?? ''}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {w.operations.length === 0 && <p className="muted">Not released: no operations yet.</p>}
    </>
  )
}

export default function App() {
  const [id, setId] = useState<string | null>(() => location.hash.slice(1) || null)
  useEffect(() => { location.hash = id ?? '' }, [id])
  const planner = usePlanner()
  return (
    <main>
      <div className="bar">
        <h1 style={{ margin: 0 }}>Work orders</h1>
        <label style={{ marginLeft: 'auto' }}>Planner <select value={planner.id} onChange={e => planner.setId(Number(e.target.value))}>
          {planner.planners.map(p => <option key={p.businessEntityId} value={p.businessEntityId}>{p.name}</option>)}
        </select></label>
      </div>
      {id ? <Detail id={id} back={() => setId(null)} planner={planner.id} /> : <List open={setId} planner={planner.id} />}
    </main>
  )
}
