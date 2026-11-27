<script lang="ts">
  // The import webhook for one Sonarr/Radarr connection: the URL and credentials to paste into the
  // manager, the password lifecycle, and what happened to the last delivery. The password exists in
  // the browser only between being created and this panel closing; the server keeps a fingerprint.
  import { api, type ArrConnection, type ArrWebhookPassword } from '../api'
  import { formatDateTime } from '../format'
  import { i18n, t } from '../i18n/i18n.svelte'

  let { connection, onChange }: { connection: ArrConnection; onChange: () => void | Promise<void> } = $props()

  let issued = $state<ArrWebhookPassword | null>(null)
  let busy = $state(false)
  let error = $state<string | null>(null)
  let copied = $state<string | null>(null)

  // The address the manager must reach. Only the browser knows how this instance was reached, so a
  // reverse proxy's hostname is used rather than the container's own idea of its address.
  let webhookUrl = $derived(typeof location === 'undefined' ? connection.webhookPath : location.origin + connection.webhookPath)
  // Clipboard access needs a secure context; over plain http the fields stay selectable instead.
  const canCopy = typeof navigator !== 'undefined' && !!navigator.clipboard?.writeText
  let showsCredentials = $derived(connection.webhookEnabled || issued !== null)

  async function createPassword() {
    if (connection.webhookEnabled && !confirm(t(i18n.m.settings.arr_webhook_replace_confirm, { type: connection.type }))) return
    busy = true
    error = null
    try {
      issued = await api.createArrWebhookPassword(connection.id)
      await onChange()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.settings.error_webhook_password
    } finally {
      busy = false
    }
  }

  async function turnOff() {
    if (!confirm(t(i18n.m.settings.arr_webhook_turn_off_confirm, { type: connection.type }))) return
    busy = true
    error = null
    try {
      await api.deleteArrWebhookPassword(connection.id)
      issued = null
      await onChange()
    } catch (err) {
      error = err instanceof Error ? err.message : i18n.m.settings.error_webhook_password
    } finally {
      busy = false
    }
  }

  async function copy(field: string, value: string) {
    try {
      await navigator.clipboard.writeText(value)
      copied = field
      setTimeout(() => { if (copied === field) copied = null }, 2000)
    } catch {
      copied = null
    }
  }

  function selectAll(event: FocusEvent) {
    (event.currentTarget as HTMLInputElement).select()
  }
</script>

{#snippet credential(field: string, label: string, value: string)}
  <div>
    <label class="label" for={`arr-webhook-${field}-${connection.id}`}>{label}</label>
    <div class="flex min-w-0 items-center gap-2">
      <input
        id={`arr-webhook-${field}-${connection.id}`}
        class="input min-w-0 flex-1 font-mono"
        readonly
        value={value}
        onfocus={selectAll}
      />
      {#if canCopy}
        <button type="button" class="btn btn-sm btn-ghost shrink-0" onclick={() => copy(field, value)}>
          {copied === field ? i18n.m.settings.arr_webhook_copied : i18n.m.settings.arr_webhook_copy}
        </button>
      {/if}
    </div>
  </div>
{/snippet}

<section class="mt-4 border-t border-line-soft pt-4" data-testid="arr-webhook" aria-labelledby={`arr-webhook-title-${connection.id}`}>
  <h4 id={`arr-webhook-title-${connection.id}`} class="text-sm font-semibold text-ink-2">{i18n.m.settings.arr_webhook_title}</h4>
  <p class="mt-1 max-w-3xl text-sm text-ink-3">{t(i18n.m.settings.arr_webhook_desc, { type: connection.type })}</p>

  {#if error}
    <div class="callout tone-bad mt-3">{error}</div>
  {/if}

  {#if !showsCredentials}
    <p class="mt-3 text-sm text-ink-3">{i18n.m.settings.arr_webhook_off}</p>
    <button type="button" class="btn btn-primary mt-2" onclick={createPassword} disabled={busy}>{i18n.m.settings.arr_webhook_create}</button>
  {:else}
    <div class="mt-3 grid max-w-3xl gap-3">
      {@render credential('url', i18n.m.settings.arr_webhook_url, webhookUrl)}
      <p class="-mt-2 text-xs text-ink-4">{t(i18n.m.settings.arr_webhook_url_hint, { type: connection.type })}</p>
      <div class="grid gap-3 sm:grid-cols-2">
        {@render credential('username', i18n.m.settings.arr_webhook_username, issued?.username ?? 'optimisarr')}
        {#if issued}
          {@render credential('password', i18n.m.settings.arr_webhook_password, issued.password)}
        {:else}
          <div>
            <span class="label">{i18n.m.settings.arr_webhook_password}</span>
            <p class="text-sm text-ink-3">{i18n.m.settings.arr_webhook_password_set}</p>
          </div>
        {/if}
      </div>
      {#if issued}
        <div class="callout tone-warn text-sm" role="status">{i18n.m.settings.arr_webhook_password_once}</div>
      {/if}
    </div>

    <div class="mt-3 flex flex-wrap gap-2">
      <button type="button" class="btn btn-sm btn-ghost" onclick={createPassword} disabled={busy}>{i18n.m.settings.arr_webhook_replace}</button>
      <button type="button" class="btn btn-sm btn-ghost text-bad" onclick={turnOff} disabled={busy}>{i18n.m.settings.arr_webhook_turn_off}</button>
    </div>

    <div class="mt-4">
      <h5 class="text-xs font-semibold text-ink-3">{t(i18n.m.settings.arr_webhook_howto_title, { type: connection.type })}</h5>
      <ol class="mt-1 list-decimal space-y-1 pl-5 text-sm text-ink-3">
        <li>{t(i18n.m.settings.arr_webhook_step_open, { type: connection.type })}</li>
        <li>{i18n.m.settings.arr_webhook_step_triggers}</li>
        <li>{i18n.m.settings.arr_webhook_step_fields}</li>
        <li>{i18n.m.settings.arr_webhook_step_test}</li>
      </ol>
    </div>
  {/if}

  <div class="mt-4" data-testid="arr-webhook-last">
    <h5 class="text-xs font-semibold text-ink-3">{i18n.m.settings.arr_webhook_last}</h5>
    {#if connection.webhookLastReceivedAt && connection.webhookLastOutcome}
      <p class="mt-1 text-sm text-ink-2">
        <span class="font-medium">{i18n.m.settings[`arr_webhook_outcome_${connection.webhookLastOutcome}`]}</span>
        <span class="text-ink-4"> · {formatDateTime(connection.webhookLastReceivedAt, new Date(), i18n.locale)}</span>
      </p>
      {#if connection.webhookLastDetail}
        <p class="mt-0.5 break-words font-mono text-caption text-ink-4">{connection.webhookLastDetail}</p>
      {/if}
    {:else}
      <p class="mt-1 text-sm text-ink-4">{i18n.m.settings.arr_webhook_last_never}</p>
    {/if}
  </div>
</section>
