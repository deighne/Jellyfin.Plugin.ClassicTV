// Classic TV channel-management controller.
// Loaded by the Jellyfin dashboard via data-controller="__plugin/classictvjs".
// The React dashboard does not execute inline <script> in injected config pages,
// so the page logic lives here (same pattern as the official plugins).
export default function (view, params) {
  (function () {
    'use strict';

    // -----------------------------------------------------------------------
    // State
    // -----------------------------------------------------------------------
    const state = {
      channels: [],      // Array<ChannelConfig> — authoritative local copy
      dirty: false,      // true while unsaved edits exist in the editor
      seriesCache: null, // Cached series list for current page session
      moviesCache: null, // Cached movie list for current page session
    };

    // -----------------------------------------------------------------------
    // API layer
    // -----------------------------------------------------------------------
    const api = {
      loadChannels() {
        return fetch('/ClassicTV/manage/channels', {
          headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { if (!r.ok) throw r; return r.json(); });
      },

      saveChannels(body) {
        return fetch('/ClassicTV/manage/channels', {
          method: 'POST',
          headers: {
            'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken(),
            'Content-Type': 'application/json'
          },
          body: JSON.stringify(body)
        }).then(function (r) { if (!r.ok) throw r; });
      },

      // Jellyfin API responses are PascalCase ({"Series":[{"Id","Name","EpisodeCount"}]}),
      // so normalise to lowercase field names here. Returns an array of
      // { id, name, episodeCount }. Duplicate library entries (same name and episode
      // count, different GUIDs) are collapsed to one.
      loadSeries() {
        return fetch('/ClassicTV/manage/library/series', {
          headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { if (!r.ok) throw r; return r.json(); })
          .then(function (data) {
            var raw = (data && (data.Series || data.series)) || (Array.isArray(data) ? data : []);
            var seen = {};
            var out = [];
            raw.forEach(function (s) {
              var item = {
                id: s.Id || s.id,
                name: s.Name || s.name,
                episodeCount: (s.EpisodeCount !== undefined ? s.EpisodeCount : s.episodeCount),
                genres: Array.isArray(s.Genres || s.genres) ? (s.Genres || s.genres) : [],
                officialRating: s.OfficialRating || s.officialRating || ''
              };
              var key = item.name + '|' + item.episodeCount;
              if (item.id && !seen[key]) { seen[key] = true; out.push(item); }
            });
            return out;
          });
      },

      // Normalises {"Episodes":[{"Id","Name","Season","Episode"}]} to
      // an array of { id, name, season, episode }.
      loadEpisodes(seriesId) {
        return fetch('/ClassicTV/manage/library/series/' + encodeURIComponent(seriesId) + '/episodes', {
          headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { if (!r.ok) throw r; return r.json(); })
          .then(function (data) {
            var raw = (data && (data.Episodes || data.episodes)) || (Array.isArray(data) ? data : []);
            return raw.map(function (e) {
              return {
                id: e.Id || e.id,
                name: e.Name || e.name,
                season: (e.Season !== undefined ? e.Season : e.season),
                episode: (e.Episode !== undefined ? e.Episode : e.episode)
              };
            });
          });
      },

      // Normalises {"Movies":[{"Id","Name","Year","DurationSeconds"}]} to
      // an array of { id, name, year, durationSeconds }.
      loadMovies() {
        return fetch('/ClassicTV/manage/library/movies', {
          headers: { 'Authorization': 'MediaBrowser Token=' + ApiClient.accessToken() }
        }).then(function (r) { if (!r.ok) throw r; return r.json(); })
          .then(function (data) {
            var raw = (data && (data.Movies || data.movies)) || (Array.isArray(data) ? data : []);
            return raw.map(function (m) {
              return {
                id: m.Id || m.id,
                name: m.Name || m.name,
                year: (m.Year !== undefined ? m.Year : m.year),
                durationSeconds: (m.DurationSeconds !== undefined ? m.DurationSeconds : m.durationSeconds) || 0,
                genres: Array.isArray(m.Genres || m.genres) ? (m.Genres || m.genres) : [],
                officialRating: m.OfficialRating || m.officialRating || ''
              };
            }).filter(function (m) { return !!m.id; });
          });
      }
    };

    // -----------------------------------------------------------------------
    // UI helpers (stubs for tasks not yet implemented)
    //   ui.openChannelEditor  — filled by task 7.4
    //   ui.showConfirm        — filled by task 7.8
    //   ui.showWarning        — filled by task 7.8
    //   ui.showFullPageError  — filled by task 7.8
    // -----------------------------------------------------------------------
    const ui = {

      // -- forward-reference stubs; later tasks replace these on the same object --

      openChannelEditor(channel) {
        console.log('ClassicTV: openChannelEditor (stub)', channel);
      },

      showConfirm(message, onConfirm) {
        // Stub — replaced by task 7.8
        if (window.confirm(message)) { onConfirm(); }
      },

      showSaveToast(message) {
        var toast = document.getElementById('ctv-save-toast');
        if (!toast) return;
        toast.textContent = message;
        toast.style.display = '';
        clearTimeout(toast._hideTimer);
        toast._hideTimer = setTimeout(function () { toast.style.display = 'none'; }, 4500);
      },

      showWarning(message) {
        // Stub — replaced by task 7.8
        var banner = document.getElementById('ctv-error-banner');
        banner.textContent = message;
        banner.style.display = '';
        setTimeout(function () { banner.style.display = 'none'; }, 5000);
      },

      showFullPageError(message, onRetry) {
        // Stub — replaced by task 7.8
        var list = document.getElementById('ctv-channel-list');
        var banner = document.getElementById('ctv-error-banner');
        banner.textContent = message;
        banner.style.display = '';
        list.innerHTML = '';
        if (onRetry) {
          var btn = document.createElement('button');
          btn.textContent = 'Retry';
          btn.className = 'raised button-submit';
          btn.addEventListener('click', onRetry);
          list.appendChild(btn);
        }
      },

      // ------------------------------------------------------------------
      // Task 7.3: renderChannelList
      // ------------------------------------------------------------------

      /**
       * Build a plain-text block summary for one block:
       *   "Block Name: Show A, Show B, Show C, ..."
       * Up to 3 show names; appends "..." if there are more.
       */
      _blockSummaryText(block) {
        var shows = block.shows || [];
        var names = shows.slice(0, 3).map(function (s) { return s.name || '(unnamed)'; });
        var suffix = shows.length > 3 ? ', \u2026' : '';  // …
        return (block.name || '(unnamed block)') + ': ' + names.join(', ') + suffix;
      },

      /**
       * Re-render the full channel list from state.channels.
       * Called after every load or mutation that changes state.channels.
       */
      renderChannelList() {
        var container = document.getElementById('ctv-channel-list');
        container.innerHTML = '';

        if (state.channels.length === 0) {
          var empty = document.createElement('p');
          empty.textContent = 'No channels configured. Click "+ New Channel" to create one.';
          empty.style.color = 'var(--text-color-secondary, #aaa)';
          container.appendChild(empty);
          return;
        }

        // Sort: non-zero numbers ascending, zero/"unassigned" last (Req 1.2)
        var sorted = state.channels.slice().sort(function (a, b) {
          var na = (a.number === 0) ? Infinity : a.number;
          var nb = (b.number === 0) ? Infinity : b.number;
          return na - nb;
        });

        sorted.forEach(function (channel) {
          container.appendChild(ui._buildChannelCard(channel));
        });
      },

      /**
       * Build and return a single channel card DOM element.
       */
      _buildChannelCard(channel) {
        var card = document.createElement('div');
        card.className = 'ctv-channel-card';
        card.dataset.channelId = channel.id;

        // --- Header row: name, number, enabled badge ---
        var header = document.createElement('div');
        header.className = 'ctv-channel-card-header';

        var title = document.createElement('span');
        title.className = 'ctv-channel-card-title';
        title.textContent = channel.name || '(unnamed)';
        header.appendChild(title);

        var numSpan = document.createElement('span');
        numSpan.className = 'ctv-channel-card-number';
        numSpan.textContent = channel.number ? 'Ch ' + channel.number : 'Unassigned';
        header.appendChild(numSpan);

        var badge = document.createElement('span');
        badge.className = 'ctv-enabled-badge' + (channel.enabled === false ? ' disabled' : '');
        badge.textContent = channel.enabled === false ? 'Disabled' : 'Enabled';
        header.appendChild(badge);

        if (channel.guide) {
          var guideBadge = document.createElement('span');
          guideBadge.className = 'ctv-mode-badge';
          guideBadge.textContent = 'Guide';
          header.appendChild(guideBadge);
        } else if (channel.mirrorUser) {
          var mirrorBadge = document.createElement('span');
          mirrorBadge.className = 'ctv-mode-badge';
          mirrorBadge.textContent = 'Mirror: ' + channel.mirrorUser;
          header.appendChild(mirrorBadge);
        }

        if (channel.maxRating) {
          var ratingBadge = document.createElement('span');
          ratingBadge.className = 'ctv-mode-badge';
          ratingBadge.textContent = 'Max: ' + channel.maxRating;
          header.appendChild(ratingBadge);
        }

        card.appendChild(header);

        // --- Block & item count summary (Req 1.3) ---
        var blocks = channel.blocks || [];
        var totalShows = 0;
        var totalEpisodes = 0;
        blocks.forEach(function (b) {
          if (!b.offline && Array.isArray(b.shows)) {
            totalShows += b.shows.length;
            b.shows.forEach(function (s) {
              totalEpisodes += Array.isArray(s.jellyfinIds) ? s.jellyfinIds.length : 0;
            });
          }
        });
        if (blocks.length > 0) {
          var blockSummary = document.createElement('div');
          blockSummary.className = 'ctv-channel-card-blocks';
          var summaryLines = blocks.map(function (b) { return ui._blockSummaryText(b); });
          var countsText = (!channel.guide && !channel.mirrorUser)
            ? ' (' + totalShows + ' shows, ' + totalEpisodes + ' items)'
            : '';
          blockSummary.textContent = summaryLines.join(' | ') + countsText;
          card.appendChild(blockSummary);
        }

        // --- Live Now Playing preview for enabled channels ---
        if (channel.enabled !== false) {
          var nowEl = document.createElement('div');
          nowEl.className = 'ctv-now-playing';
          nowEl.textContent = 'Checking schedule…';
          card.appendChild(nowEl);
          fetch('/ClassicTV/channel/' + encodeURIComponent(channel.id))
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (slot) {
              if (!nowEl.isConnected) return;
              if (!slot || slot.IsOffline) {
                nowEl.textContent = 'Currently offline';
                return;
              }
              nowEl.textContent = 'Now playing: ';
              var strong = document.createElement('strong');
              strong.textContent = slot.Title || 'On Air';
              nowEl.appendChild(strong);
            })
            .catch(function () {
              if (nowEl.isConnected) { nowEl.textContent = ''; }
            });
        }

        // --- Action buttons ---
        var actions = document.createElement('div');
        actions.className = 'ctv-channel-card-actions';

        // Edit button (Req 1.5)
        var editBtn = document.createElement('button');
        editBtn.className = 'raised button-submit';
        editBtn.textContent = 'Edit';
        editBtn.addEventListener('click', function () {
          ui.openChannelEditor(channel);
        });
        actions.appendChild(editBtn);

        // Duplicate button
        var dupBtn = document.createElement('button');
        dupBtn.className = 'raised';
        dupBtn.textContent = 'Duplicate';
        dupBtn.addEventListener('click', function () {
          var clone = JSON.parse(JSON.stringify(channel));
          var maxNum = state.channels.reduce(function (m, c) {
            return Math.max(m, Number(c.number) || 0);
          }, 0);
          var baseId = (channel.id || 'channel') + '-copy';
          var candidateId = baseId;
          var suffix = 2;
          while (state.channels.some(function (c) { return c.id === candidateId; })) {
            candidateId = baseId + '-' + suffix;
            suffix += 1;
          }
          clone.id = candidateId;
          clone.name = (channel.name || 'Channel') + ' (Copy)';
          clone.number = maxNum + 1;
          ui.openChannelEditor(clone, { asNew: true });
        });
        actions.appendChild(dupBtn);

        // Enable/Disable toggle (Req 1.7)
        var toggleBtn = document.createElement('button');
        toggleBtn.className = 'raised';
        toggleBtn.textContent = channel.enabled === false ? 'Enable' : 'Disable';
        toggleBtn.addEventListener('click', function () {
          toggleEnabled(channel.id);
        });
        actions.appendChild(toggleBtn);

        // Delete button (Req 1.6)
        var deleteBtn = document.createElement('button');
        deleteBtn.className = 'raised button-cancel';
        deleteBtn.textContent = 'Delete';
        deleteBtn.addEventListener('click', function () {
          ui.showConfirm(
            'Delete channel "' + (channel.name || channel.id) + '"? This cannot be undone.',
            function () { deleteChannel(channel.id); }
          );
        });
        actions.appendChild(deleteBtn);

        card.appendChild(actions);
        return card;
      }

    }; // end ui

    // -----------------------------------------------------------------------
    // toggleEnabled — flip a channel's enabled flag and save (Req 1.7)
    // -----------------------------------------------------------------------
    function toggleEnabled(channelId) {
      // Snapshot for rollback
      var snapshot = JSON.parse(JSON.stringify(state.channels));

      var ch = state.channels.find(function (c) { return c.id === channelId; });
      if (!ch) return;
      ch.enabled = !ch.enabled;

      // Re-render immediately so the toggle feels responsive
      ui.renderChannelList();

      api.saveChannels({ channels: state.channels })
        .catch(function (err) {
          console.error('ClassicTV: toggleEnabled save failed', err);
          state.channels = snapshot;
          ui.renderChannelList();
          ui.showWarning('Could not save channel state. Changes reverted.');
        });
    }

    // -----------------------------------------------------------------------
    // deleteChannel — remove a channel and save (Req 1.6)
    // -----------------------------------------------------------------------
    function deleteChannel(channelId) {
      var snapshot = JSON.parse(JSON.stringify(state.channels));

      state.channels = state.channels.filter(function (c) { return c.id !== channelId; });
      ui.renderChannelList();

      api.saveChannels({ channels: state.channels })
        .catch(function (err) {
          console.error('ClassicTV: deleteChannel save failed', err);
          state.channels = snapshot;
          ui.renderChannelList();
          ui.showWarning('Could not delete channel. Changes reverted.');
        });
    }

    // -----------------------------------------------------------------------
    // Page initialisation (Req 1.1, 1.8)
    // -----------------------------------------------------------------------
    // Attach to the page element itself, not document. Jellyfin dispatches the
    // 'pageshow' custom event on the page container and it does not bubble, so a
    // document-level listener never fires (the page would render blank).
    // This mirrors the official plugin template.
    view.addEventListener('viewshow', function () {
      // Hide any stale error banner from a previous load
      var banner = document.getElementById('ctv-error-banner');
      banner.style.display = 'none';

      api.loadChannels()
        .then(function (data) {
          state.channels = data.channels || [];
          ui.renderChannelList();
        })
        .catch(function (err) {
          console.error('ClassicTV: loadChannels error', err);
          var msg = 'Failed to load channels. Check the console for details.';
          ui.showFullPageError(msg, function () {
            // Retry: re-fire the load sequence
            api.loadChannels()
              .then(function (data) {
                state.channels = data.channels || [];
                banner.style.display = 'none';
                ui.renderChannelList();
              })
              .catch(function (err2) {
                console.error('ClassicTV: retry loadChannels error', err2);
                ui.showFullPageError(msg, null);
              });
          });
        });
    });

    // -----------------------------------------------------------------------
    // New Channel button (Req 1.4)
    // -----------------------------------------------------------------------
    document.getElementById('ctv-btn-new-channel').addEventListener('click', function () {
      ui.openChannelEditor(null);
    });

    // -----------------------------------------------------------------------
    // Expose internals for later tasks (7.4–7.8 extend this object)
    // -----------------------------------------------------------------------
    window._ctv = { state: state, api: api, ui: ui };

  })();
  (function () {
    'use strict';

    // Wait until the 7.2/7.3 IIFE has run and published window._ctv,
    // then augment the ui object in-place so all modules share one reference.
    var _ctv  = window._ctv;
    var state = _ctv.state;
    var api   = _ctv.api;
    var ui    = _ctv.ui;

    // -----------------------------------------------------------------------
    // Slug helper
    // -----------------------------------------------------------------------
    function toSlug(name) {
      return name.toLowerCase().replace(/\s+/g, '-').replace(/[^a-z0-9-]/g, '');
    }

    // -----------------------------------------------------------------------
    // Attribute-value escape helper
    // -----------------------------------------------------------------------
    function _escAttr(str) {
      return String(str == null ? '' : str)
        .replace(/&/g, '&amp;')
        .replace(/"/g, '&quot;');
    }

    // -----------------------------------------------------------------------
    // saveChannelDraft — stub for task 7.4; replaced by task 7.5 on window._ctv
    // -----------------------------------------------------------------------
    function saveChannelDraft(draft, editingId) {
      // Delegate to the task-7.5 implementation when present
      if (typeof _ctv.saveChannelDraft === 'function') {
        _ctv.saveChannelDraft(draft, editingId);
        return;
      }

      // Minimal fallback: update state + persist
      var snapshot = JSON.parse(JSON.stringify(state.channels));
      if (editingId === null) {
        state.channels.push(draft);
      } else {
        var idx = state.channels.findIndex(function (c) { return c.id === editingId; });
        if (idx >= 0) { state.channels[idx] = draft; } else { state.channels.push(draft); }
      }

      api.saveChannels({ channels: state.channels })
        .then(function () {
          ui.closeEditor();
          ui.renderChannelList();
        })
        .catch(function (err) {
          console.error('ClassicTV: saveChannels error', err);
          state.channels = snapshot;
          var errDiv = document.getElementById('ctv-validation-errors');
          if (errDiv) {
            errDiv.innerHTML = '<span class="ctv-error">Save failed. Please try again.</span>';
          }
        });
    }

    // -----------------------------------------------------------------------
    // closeEditor
    // -----------------------------------------------------------------------
    ui.closeEditor = function () {
      document.getElementById('ctv-editor-overlay').style.display = 'none';
    };

    // -----------------------------------------------------------------------
    // openChannelEditor
    //   channel — existing ChannelConfig object (edit), or null (new)
    // -----------------------------------------------------------------------
    ui.openChannelEditor = function (channel, options) {
      var asNew     = Boolean(options && options.asNew);
      var isNew     = (channel === null) || asNew;
      var editingId = isNew ? null : channel.id;

      // Deep-clone so in-editor edits don't mutate state.channels until save
      var draft = (channel === null)
        ? { id: '', name: '', number: 0, enabled: true, maxRating: null, allowUnrated: true, startAnchor: '2024-01-01T00:00:00Z', blocks: [] }
        : JSON.parse(JSON.stringify(channel));

      // idManuallyEdited: for brand-new channels, auto-slug fires until the user
      // manually types in the ID field; for duplicated/existing channels, keep preset ID.
      var idManuallyEdited = (channel !== null);

      // Render the form HTML
      var content = document.getElementById('ctv-editor-content');
      content.innerHTML = [
        '<h2 style="margin-top:0;">' + (isNew ? 'New Channel' : 'Edit Channel') + '</h2>',

        // Validation error container — empty until first save attempt
        '<div id="ctv-validation-errors"></div>',

        // ---- Channel Name -----------------------------------------------
        '<div class="ctv-form-row">',
        '  <label for="ctv-field-name">Channel Name</label>',
        '  <input id="ctv-field-name" type="text" maxlength="100"',
        '    value="' + _escAttr(draft.name) + '" autocomplete="off">',
        '</div>',

        // ---- Channel ID -------------------------------------------------
        '<div class="ctv-form-row">',
        '  <label for="ctv-field-id">',
        '    Channel ID',
        '    <span class="ctv-tooltip-icon"',
        '      title="URL-safe slug — lowercase letters, numbers, and hyphens only.">ⓘ</span>',
        '  </label>',
        '  <input id="ctv-field-id" type="text"',
        '    value="' + _escAttr(draft.id) + '" autocomplete="off"'
          + (isNew ? '' : ' readonly') + '>',
        isNew ? '' : '<small style="color:var(--text-color-secondary,#aaa);font-size:0.8em;">Channel ID cannot be changed after creation.</small>',
        '</div>',

        // ---- Channel Number ----------------------------------------------
        '<div class="ctv-form-row">',
        '  <label for="ctv-field-number">',
        '    Channel Number',
        '    <span class="ctv-tooltip-icon"',
        '      title="Used for remote digit entry and channel sort order. 0 = unassigned.">ⓘ</span>',
        '  </label>',
        '  <input id="ctv-field-number" type="number" min="0" max="9999"',
        '    value="' + Number(draft.number || 0) + '">',
        '</div>',

        // ---- Enabled checkbox -------------------------------------------
        '<div class="ctv-form-row">',
        '  <div class="ctv-form-inline">',
        '    <input id="ctv-field-enabled" type="checkbox"' + (draft.enabled !== false ? ' checked' : '') + '>',
        '    <label for="ctv-field-enabled">Enabled</label>',
        '  </div>',
        '</div>',

        // ---- Content Rating Restriction ---------------------------------
        '<div class="ctv-form-row">',
        '  <label for="ctv-field-max-rating">Max Content Rating</label>',
        '  <div class="ctv-form-inline" style="flex-wrap:wrap;gap:0.75em;">',
        '    <select id="ctv-field-max-rating">',
        '      <option value=""' + (!draft.maxRating ? ' selected' : '') + '>Unrestricted</option>',
        '      <option value="G"' + (draft.maxRating === 'G' ? ' selected' : '') + '>G / TV-G / TV-Y</option>',
        '      <option value="PG"' + (draft.maxRating === 'PG' ? ' selected' : '') + '>PG / TV-PG</option>',
        '      <option value="PG-13"' + (draft.maxRating === 'PG-13' ? ' selected' : '') + '>PG-13 / TV-14</option>',
        '      <option value="R"' + (draft.maxRating === 'R' ? ' selected' : '') + '>R / TV-MA</option>',
        '      <option value="NC-17"' + (draft.maxRating === 'NC-17' ? ' selected' : '') + '>NC-17</option>',
        '    </select>',
        '    <label style="display:inline-flex;align-items:center;gap:0.35em;cursor:pointer;">',
        '      <input id="ctv-field-allow-unrated" type="checkbox"' + (draft.allowUnrated !== false ? ' checked' : '') + '>',
        '      <span>Allow Unrated</span>',
        '    </label>',
        '  </div>',
        '</div>',

        // ---- Advanced (collapsed) ----------------------------------------
        '<details id="ctv-advanced-details">',
        '  <summary style="cursor:pointer;color:var(--text-color-secondary,#aaa);',
        '    font-size:0.9em;margin-bottom:0.5em;">Advanced</summary>',
        '  <div class="ctv-form-row" style="margin-top:0.5em;">',
        '    <label for="ctv-field-anchor">',
        '      Start Anchor',
        '      <span class="ctv-tooltip-icon"',
        '        title="ISO 8601 UTC timestamp. Determines which episode plays first.',
        '               Changing this shifts the entire rotation.">ⓘ</span>',
        '    </label>',
        '    <input id="ctv-field-anchor" type="text"',
        '      value="' + _escAttr(draft.startAnchor || '2024-01-01T00:00:00Z') + '"',
        '      placeholder="2024-01-01T00:00:00Z" autocomplete="off">',
        '    <div style="display:flex;gap:0.5em;margin-top:0.4em;">',
        '      <button id="ctv-btn-anchor-now"  type="button" class="raised">Reset to now</button>',
        '      <button id="ctv-btn-anchor-2024" type="button" class="raised">Reset to Jan 1 2024</button>',
        '    </div>',
        '  </div>',
        '</details>',

        '<hr class="ctv-sep">',

        // ---- Block section (populated by task 7.6) -----------------------
        '<div id="ctv-block-section">',
        '  <h3 style="margin:0 0 0.5em;">Blocks</h3>',
        '  <div id="ctv-block-timeline" class="ctv-timeline-wrap"></div>',
        '  <ul id="ctv-block-list"></ul>',
        '  <button id="ctv-btn-add-block" type="button" class="raised">+ Add Block</button>',
        '</div>',

        '<hr class="ctv-sep">',

        // ---- Footer actions ----------------------------------------------
        '<div style="display:flex;gap:0.5em;flex-wrap:wrap;">',
        '  <button id="ctv-btn-editor-save"   type="button" class="raised button-submit">Save</button>',
        '  <button id="ctv-btn-editor-cancel" type="button" class="raised">Cancel</button>',
        '</div>'
      ].join('\n');

      // Show the overlay
      document.getElementById('ctv-editor-overlay').style.display = '';

      // ----------------------------------------------------------------
      // Wire up event handlers
      // ----------------------------------------------------------------
      var nameInput   = document.getElementById('ctv-field-name');
      var idInput     = document.getElementById('ctv-field-id');
      var numberInput    = document.getElementById('ctv-field-number');
      var enabledCb      = document.getElementById('ctv-field-enabled');
      var maxRatingSel   = document.getElementById('ctv-field-max-rating');
      var allowUnratedCb = document.getElementById('ctv-field-allow-unrated');
      var anchorInput    = document.getElementById('ctv-field-anchor');

      if (maxRatingSel) {
        maxRatingSel.addEventListener('change', function () {
          draft.maxRating = maxRatingSel.value || null;
        });
      }
      if (allowUnratedCb) {
        allowUnratedCb.addEventListener('change', function () {
          draft.allowUnrated = allowUnratedCb.checked;
        });
      }

      // Auto-slug: only active for new channels where ID hasn't been manually set
      if (isNew) {
        nameInput.addEventListener('input', function () {
          if (!idManuallyEdited) {
            idInput.value = toSlug(nameInput.value);
          }
        });

        idInput.addEventListener('input', function () {
          idManuallyEdited = true;
        });
      }

      // Reset to now (Req 2.9)
      document.getElementById('ctv-btn-anchor-now').addEventListener('click', function () {
        // Strip milliseconds for a clean ISO 8601 UTC string: 2026-09-20T14:30:00Z
        anchorInput.value = new Date().toISOString().replace(/\.\d+Z$/, 'Z');
      });

      // Reset to Jan 1 2024 (Req 2.10)
      document.getElementById('ctv-btn-anchor-2024').addEventListener('click', function () {
        anchorInput.value = '2024-01-01T00:00:00Z';
      });

      // Save: collect field values → hand to saveChannelDraft (Req 2.6)
      document.getElementById('ctv-btn-editor-save').addEventListener('click', function () {
        draft.name        = nameInput.value.trim();
        draft.id          = isNew ? idInput.value.trim() : editingId;
        draft.number      = parseInt(numberInput.value, 10);
        if (isNaN(draft.number)) { draft.number = 0; }
        draft.enabled     = enabledCb.checked;
        draft.maxRating   = maxRatingSel ? (maxRatingSel.value || null) : (draft.maxRating || null);
        draft.allowUnrated = allowUnratedCb ? allowUnratedCb.checked : !!draft.allowUnrated;
        draft.startAnchor = anchorInput.value.trim();
        // draft.blocks are maintained by task 7.6 directly in the draft object

        saveChannelDraft(draft, editingId);
      });

      var initialSnapshot = JSON.stringify({
        name: nameInput.value.trim(),
        id: isNew ? idInput.value.trim() : editingId,
        number: parseInt(numberInput.value, 10) || 0,
        enabled: enabledCb.checked,
        maxRating: maxRatingSel ? (maxRatingSel.value || null) : null,
        allowUnrated: allowUnratedCb ? allowUnratedCb.checked : true,
        startAnchor: anchorInput.value.trim(),
        blocks: draft.blocks || []
      });

      // Cancel (Req 2.1 — close without saving, with unsaved-changes guard)
      document.getElementById('ctv-btn-editor-cancel').addEventListener('click', function () {
        var currentSnapshot = JSON.stringify({
          name: nameInput.value.trim(),
          id: isNew ? idInput.value.trim() : editingId,
          number: parseInt(numberInput.value, 10) || 0,
          enabled: enabledCb.checked,
          maxRating: maxRatingSel ? (maxRatingSel.value || null) : null,
          allowUnrated: allowUnratedCb ? allowUnratedCb.checked : true,
          startAnchor: anchorInput.value.trim(),
          blocks: draft.blocks || []
        });
        if (currentSnapshot !== initialSnapshot) {
          ui.showConfirm('Discard unsaved changes to this channel?', function () {
            ui.closeEditor();
          });
        } else {
          ui.closeEditor();
        }
      });

      // Expose live draft + editingId so tasks 7.5 and 7.6 can access them
      _ctv._editorDraft     = draft;
      _ctv._editorEditingId = editingId;

      // If task 7.6 is already loaded and provides a block renderer, invoke it
      if (typeof ui.renderBlockList === 'function') {
        ui.renderBlockList(draft);
      }
    };

  })();
  (function () {
    'use strict';

    var _ctv  = window._ctv;
    var state = _ctv.state;
    var api   = _ctv.api;
    var ui    = _ctv.ui;

    // -----------------------------------------------------------------------
    // validate object
    // -----------------------------------------------------------------------
    var validate = {

      /**
       * Validate a channel draft before saving.
       * All checks run independently — all errors collected simultaneously.
       *
       * @param {object} draft       - the channel being saved
       * @param {Array}  allChannels - current state.channels
       * @param {string|null} editingId - ID of the channel being edited (null = new)
       * @returns {{ valid: boolean, errors: string[] }}
       */
      validateChannel: function (draft, allChannels, editingId) {
        var errors = [];

        // ---- Channel ID checks (Req 2.3, 11.2, 11.3) --------------------

        var channelId = (draft.id || '').trim();

        if (!channelId) {
          errors.push('Channel ID is required.');
        } else {
          if (!/^[a-z0-9-]+$/.test(channelId)) {
            errors.push('Channel ID must contain only lowercase letters, numbers, and hyphens.');
          }
          // Duplicate ID check — exclude the channel currently being edited
          var duplicate = allChannels.some(function (c) {
            return c.id === channelId && c.id !== editingId;
          });
          if (duplicate) {
            errors.push('Channel ID "' + channelId + '" already exists.');
          }
        }

        // ---- Channel Number check (Req 2.4, 11.4) -----------------------

        var num = draft.number;
        if (!Number.isInteger(num) || num < 0 || num > 9999) {
          errors.push('Channel Number must be an integer from 0 to 9999.');
        } else if (num > 0 && draft.enabled !== false) {
          // Duplicate number among enabled channels — mirrors server-side
          // ChannelConfigValidator (the server rejects this with HTTP 400).
          var numConflict = allChannels.some(function (c) {
            return c.enabled !== false && c.id !== editingId && Number(c.number) === num;
          });
          if (numConflict) {
            errors.push('Channel Number ' + num + ' is already used by another enabled channel.');
          }
        }

        // ---- Block / show checks ----------------------------------------

        var blocks = draft.blocks || [];
        blocks.forEach(function (block, bi) {
          var blockLabel = '"' + (block.name || ('Block ' + (bi + 1))) + '"';

          // Offline blocks are exempt from hour-range validation (Req 11.6)
          if (!block.offline) {
            var s = block.startHour;
            var e = block.endHour;

            if (!Number.isInteger(s) || s < 0 || s > 23) {
              errors.push('Block ' + blockLabel + ': Start hour must be an integer 0–23 (got ' + s + ').');
            }
            if (!Number.isInteger(e) || e < 1 || e > 24) {
              errors.push('Block ' + blockLabel + ': End hour must be an integer 1–24 (got ' + e + ').');
            }
            // Only check end > start when both are individually valid
            if (Number.isInteger(s) && s >= 0 && s <= 23 &&
                Number.isInteger(e) && e >= 1 && e <= 24) {
              if (e <= s) {
                errors.push('Block ' + blockLabel + ': End hour (' + e + ') must be greater than Start hour (' + s + ').');
              }
            }
          }

          // Block overlap check against all earlier non-offline blocks (Req 3.5, 11)
          // Overlap: s1 < e2 && s2 < e1; adjacent [0,12) and [12,24) do NOT overlap.
          if (!block.offline) {
            for (var prev = 0; prev < bi; prev++) {
              var pb = blocks[prev];
              if (pb.offline) { continue; }
              var s1 = block.startHour, e1 = block.endHour;
              var s2 = pb.startHour,   e2 = pb.endHour;
              if (Number.isInteger(s1) && Number.isInteger(e1) &&
                  Number.isInteger(s2) && Number.isInteger(e2) &&
                  s1 < e2 && s2 < e1) {
                var prevLabel = '"' + (pb.name || ('Block ' + (prev + 1))) + '"';
                errors.push('Block ' + blockLabel + ' overlaps with block ' + prevLabel + '.');
              }
            }
          }

          // Show-level checks (Req 4.7, 4.8, 11.7, 11.8)
          var shows = block.shows || [];
          shows.forEach(function (show, si) {
            var showLabel = '"' + (show.name || ('Show ' + (si + 1))) + '"';

            // Weight 1–100
            var w = show.weight;
            if (!Number.isInteger(w) || w < 1 || w > 100) {
              errors.push(
                'Block ' + blockLabel + ', Show ' + showLabel + ': Weight must be an integer from 1 to 100 (got ' + w + ').'
              );
            }

            // Must have episode IDs (selected via picker)
            var ids = show.jellyfinIds || [];
            if (ids.length === 0) {
              errors.push(
                'Block ' + blockLabel + ', Show ' + showLabel + ': No episodes loaded. Re-add this show via the series picker.'
              );
            }
          });
        });

        return { valid: errors.length === 0, errors: errors };
      },

      /**
       * Check whether a channel draft would create a duplicate channel number
       * among enabled channels (excluding the one being edited).
       * Returns a warning string, or null if no conflict.
       *
       * @param {object} draft
       * @param {Array}  allChannels
       * @param {string|null} editingId
       * @returns {string|null}
       */
      duplicateNumberWarning: function (draft, allChannels, editingId) {
        var n = draft.number;
        if (!n || n === 0) { return null; }

        var conflict = allChannels.find(function (c) {
          return c.id !== editingId && c.enabled !== false && c.number === n;
        });

        if (conflict) {
          return 'Channel number ' + n + ' is already used by "' + (conflict.name || conflict.id) + '". Both channels will share this number.';
        }
        return null;
      }
    };

    // -----------------------------------------------------------------------
    // ui.showEditorErrors / ui.clearEditorErrors (Req 11.1, 11.9)
    // -----------------------------------------------------------------------
    ui.showEditorErrors = function (errors) {
      var container = document.getElementById('ctv-validation-errors');
      if (!container) { return; }
      if (!errors || errors.length === 0) {
        container.innerHTML = '';
        return;
      }
      var html = '<div class="ctv-error"><strong>Please fix the following errors before saving:</strong><ul>';
      errors.forEach(function (msg) {
        html += '<li>' + _escHtml(msg) + '</li>';
      });
      html += '</ul></div>';
      container.innerHTML = html;
    };

    ui.clearEditorErrors = function () {
      var container = document.getElementById('ctv-validation-errors');
      if (container) { container.innerHTML = ''; }
    };

    // Minimal HTML-escape helper for error message text
    function _escHtml(str) {
      return String(str == null ? '' : str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
    }

    // -----------------------------------------------------------------------
    // saveChannelDraft (Req 2.6, 2.7, 11.1)
    // -----------------------------------------------------------------------
    function saveChannelDraft(draft, editingId) {
      // Clear any previous errors
      ui.clearEditorErrors();

      // Run validation — collect all errors simultaneously
      var result = validate.validateChannel(draft, state.channels, editingId);
      if (!result.valid) {
        ui.showEditorErrors(result.errors);
        return;
      }

      // Non-blocking duplicate number warning (Req 2.5, 11.5)
      var warning = validate.duplicateNumberWarning(draft, state.channels, editingId);
      if (warning) {
        ui.showWarning(warning);
      }

      // Snapshot for rollback (Req 2.7)
      var snapshot = JSON.parse(JSON.stringify(state.channels));

      // Update local state
      if (editingId === null) {
        state.channels.push(draft);
      } else {
        var idx = state.channels.findIndex(function (c) { return c.id === editingId; });
        if (idx >= 0) {
          state.channels[idx] = draft;
        } else {
          state.channels.push(draft);
        }
      }

      // Strip transient _seriesId fields from shows before posting
      var payload = JSON.parse(JSON.stringify({ channels: state.channels }));
      (payload.channels || []).forEach(function (ch) {
        (ch.blocks || []).forEach(function (bl) {
          (bl.shows || []).forEach(function (sh) {
            delete sh._seriesId;
          });
        });
      });

      api.saveChannels(payload)
        .then(function () {
          ui.closeEditor();
          ui.renderChannelList();
          if (typeof ui.showSaveToast === 'function') {
            ui.showSaveToast('Channel saved — rebuilding schedule cache…');
          }
        })
        .catch(function (err) {
          console.error('ClassicTV: saveChannels error', err);
          // Restore pre-save state
          state.channels = snapshot;
          var msg = 'Save failed. The server returned an error — your changes were not saved.';
          if (err && typeof err.text === 'function') {
            err.text().then(function (body) {
              ui.showEditorErrors(['Save failed: ' + (body || 'unknown server error')]);
            }).catch(function () {
              ui.showEditorErrors([msg]);
            });
          } else {
            ui.showEditorErrors([msg]);
          }
        });
    }

    // -----------------------------------------------------------------------
    // Install on window._ctv so task 7.4's stub delegates here,
    // and tasks 7.6+ can call saveChannelDraft directly.
    // -----------------------------------------------------------------------
    _ctv.validate          = validate;
    _ctv.saveChannelDraft  = saveChannelDraft;

  })();
  (function () {
    'use strict';

    var _ctv = window._ctv;
    var ui   = _ctv.ui;

    // -----------------------------------------------------------------------
    // Block-level hour validation helper — fires on blur (Req 3.4)
    // Returns an error string or null.
    // -----------------------------------------------------------------------
    function validateBlockHours(block) {
      if (block.offline) return null; // Req 11.6: offline blocks exempt from hour validation

      var startH = block.startHour;
      var endH   = block.endHour;
      var errors = [];

      if (!Number.isInteger(startH) || startH < 0 || startH > 23) {
        errors.push('Start hour must be an integer 0\u201323.');
      }
      if (!Number.isInteger(endH) || endH < 1 || endH > 24) {
        errors.push('End hour must be an integer 1\u201324.');
      }
      if (errors.length === 0 && endH <= startH) {
        errors.push('End hour must be greater than start hour.');
      }
      return errors.length > 0 ? errors.join(' ') : null;
    }

    // -----------------------------------------------------------------------
    // Block overlap check — checks block at index bi against all other blocks
    // (Req 3.5). Returns the name of the first overlapping block or null.
    // Offline blocks are not checked for overlap.
    // -----------------------------------------------------------------------
    function findOverlap(blocks, bi) {
      var b = blocks[bi];
      if (b.offline) return null;
      for (var i = 0; i < blocks.length; i++) {
        if (i === bi) continue;
        var other = blocks[i];
        if (other.offline) continue;
        // [s1,e1) overlaps [s2,e2) iff s1 < e2 && s2 < e1
        if (b.startHour < other.endHour && other.startHour < b.endHour) {
          return other.name || ('Block ' + (i + 1));
        }
      }
      return null;
    }

    // -----------------------------------------------------------------------
    // Render the inline edit form for one block.
    // Changes are written directly into draft.blocks[bi].
    // -----------------------------------------------------------------------
    function renderBlockForm(blockBody, draft, bi) {
      var block = draft.blocks[bi];

      // Helper to show/clear inline hour error message below a field
      function setHourError(container, msg) {
        var errSpan = container.querySelector('.ctv-block-hour-error');
        if (!errSpan) {
          errSpan = document.createElement('span');
          errSpan.className = 'ctv-error ctv-block-hour-error';
          errSpan.style.fontSize = '0.8em';
          container.appendChild(errSpan);
        }
        errSpan.textContent = msg || '';
        errSpan.style.display = msg ? '' : 'none';
      }

      // ---- Block name ----
      var nameRow = document.createElement('div');
      nameRow.className = 'ctv-form-row';
      nameRow.style.marginTop = '0.6em';

      var nameLabel = document.createElement('label');
      nameLabel.textContent = 'Block name';
      nameLabel.style.fontSize = '0.875em';
      nameLabel.style.color = 'var(--text-color-secondary, #aaa)';
      nameRow.appendChild(nameLabel);

      var nameInput = document.createElement('input');
      nameInput.type = 'text';
      nameInput.maxLength = 60;
      nameInput.value = block.name || '';
      nameInput.autocomplete = 'off';
      nameInput.style.cssText = 'background:rgba(255,255,255,0.07);color:var(--theme-text-color,#fff);' +
        'border:1px solid var(--divider-color,#444);border-radius:3px;padding:0.4em 0.6em;' +
        'font-size:0.95em;max-width:360px;box-sizing:border-box;';
      nameInput.addEventListener('input', function () {
        block.name = nameInput.value;
        // Update the header title live
        var headerTitle = blockBody.parentElement.querySelector('.ctv-block-header-title');
        if (headerTitle) {
          headerTitle.textContent = block.name || '(unnamed block)';
        }
      });
      nameRow.appendChild(nameInput);
      blockBody.appendChild(nameRow);

      // ---- Start hour ----
      var startRow = document.createElement('div');
      startRow.className = 'ctv-form-row';

      var startLabel = document.createElement('label');
      startLabel.style.fontSize = '0.875em';
      startLabel.style.color = 'var(--text-color-secondary, #aaa)';
      startLabel.innerHTML = 'Start hour (Chicago time, CT) ' +
        '<span class="ctv-tooltip-icon" title="Hours are in Central Time (Chicago). ' +
        'During Daylight Saving Time transitions, displayed hours may shift by one hour.">ⓘ</span>';
      startRow.appendChild(startLabel);

      var startInput = document.createElement('input');
      startInput.type = 'number';
      startInput.min = '0';
      startInput.max = '23';
      startInput.value = block.startHour !== undefined ? block.startHour : 0;
      startInput.style.cssText = 'background:rgba(255,255,255,0.07);color:var(--theme-text-color,#fff);' +
        'border:1px solid var(--divider-color,#444);border-radius:3px;padding:0.4em 0.6em;' +
        'font-size:0.95em;max-width:100px;box-sizing:border-box;';
      startInput.addEventListener('input', function () {
        var v = parseInt(startInput.value, 10);
        block.startHour = isNaN(v) ? startInput.value : v;
        if (typeof ui.renderBlockTimeline === 'function') { ui.renderBlockTimeline(draft); }
      });
      startInput.addEventListener('blur', function () {
        var err = validateBlockHours(block);
        setHourError(startRow, err);
      });
      startRow.appendChild(startInput);
      blockBody.appendChild(startRow);

      // ---- End hour ----
      var endRow = document.createElement('div');
      endRow.className = 'ctv-form-row';

      var endLabel = document.createElement('label');
      endLabel.style.fontSize = '0.875em';
      endLabel.style.color = 'var(--text-color-secondary, #aaa)';
      endLabel.innerHTML = 'End hour (Chicago time, CT) ' +
        '<span class="ctv-tooltip-icon" title="Hours are in Central Time (Chicago). ' +
        'During Daylight Saving Time transitions, displayed hours may shift by one hour.">ⓘ</span>';
      endRow.appendChild(endLabel);

      var endInput = document.createElement('input');
      endInput.type = 'number';
      endInput.min = '1';
      endInput.max = '24';
      endInput.value = block.endHour !== undefined ? block.endHour : 24;
      endInput.style.cssText = 'background:rgba(255,255,255,0.07);color:var(--theme-text-color,#fff);' +
        'border:1px solid var(--divider-color,#444);border-radius:3px;padding:0.4em 0.6em;' +
        'font-size:0.95em;max-width:100px;box-sizing:border-box;';
      endInput.addEventListener('input', function () {
        var v = parseInt(endInput.value, 10);
        block.endHour = isNaN(v) ? endInput.value : v;
        if (typeof ui.renderBlockTimeline === 'function') { ui.renderBlockTimeline(draft); }
      });
      endInput.addEventListener('blur', function () {
        var err = validateBlockHours(block);
        setHourError(endRow, err);
        if (!err) {
          // Also check overlap on blur
          var conflictName = findOverlap(draft.blocks, bi);
          if (conflictName) {
            setHourError(endRow, 'Overlaps with block "' + conflictName + '".');
          }
        }
      });
      endRow.appendChild(endInput);
      blockBody.appendChild(endRow);

      // ---- Offline toggle ----
      var offlineRow = document.createElement('div');
      offlineRow.className = 'ctv-form-row';

      var offlineWrap = document.createElement('div');
      offlineWrap.className = 'ctv-form-inline';

      var offlineCb = document.createElement('input');
      offlineCb.type = 'checkbox';
      offlineCb.id = 'ctv-block-offline-' + bi;
      offlineCb.checked = !!block.offline;
      offlineCb.addEventListener('change', function () {
        block.offline = offlineCb.checked;
        // Re-render this block's li element so offline class/header updates
        ui.renderBlockList(draft);
      });

      var offlineLabel = document.createElement('label');
      offlineLabel.htmlFor = 'ctv-block-offline-' + bi;
      offlineLabel.textContent = 'Offline';
      offlineLabel.style.fontSize = '0.875em';

      offlineWrap.appendChild(offlineCb);
      offlineWrap.appendChild(offlineLabel);
      offlineRow.appendChild(offlineWrap);
      blockBody.appendChild(offlineRow);

      // ---- Show placeholder (task 7.7) ----
      var showPlaceholder = document.createElement('div');
      showPlaceholder.className = 'ctv-show-section';
      showPlaceholder.style.marginTop = '0.75em';
      showPlaceholder.innerHTML = '<em style="font-size:0.85em;color:var(--text-color-secondary,#aaa);">' +
        'Show management — task 7.7</em>';
      blockBody.appendChild(showPlaceholder);
    }

    // -----------------------------------------------------------------------
    // ui.renderBlockList — rebuild #ctv-block-list from draft.blocks (Req 3.1)
    // -----------------------------------------------------------------------
    ui.renderBlockTimeline = function (draft) {
      var wrap = document.getElementById('ctv-block-timeline');
      if (!wrap) return;
      wrap.innerHTML = '';

      var blocks = (draft && draft.blocks) || [];
      if (blocks.length === 0) return;

      var bar = document.createElement('div');
      bar.className = 'ctv-timeline';

      var activeRanges = [];
      blocks.forEach(function (b, idx) {
        if (!b.offline) {
          var s = Number(b.startHour);
          var e = Number(b.endHour);
          if (!isNaN(s) && !isNaN(e) && e > s) {
            activeRanges.push({ idx: idx, s: s, e: e });
          }
        }
      });

      blocks.forEach(function (b, idx) {
        var s = b.offline ? 0 : Math.max(0, Math.min(24, Number(b.startHour) || 0));
        var e = b.offline ? 24 : Math.max(0, Math.min(24, Number(b.endHour) || 0));
        if (e <= s) return;

        var hasOverlap = !b.offline && activeRanges.some(function (other) {
          return other.idx !== idx && s < other.e && other.s < e;
        });

        var seg = document.createElement('div');
        seg.className = 'ctv-timeline-seg' +
          (b.offline ? ' offline' : '') +
          (hasOverlap ? ' overlap' : '');
        seg.style.left = ((s / 24) * 100) + '%';
        seg.style.width = (((e - s) / 24) * 100) + '%';
        seg.title = (b.name || 'Block') + ' (' + s + '–' + e + 'h CT)' +
          (hasOverlap ? ' — OVERLAPS' : '');
        seg.textContent = b.name || (s + '–' + e);
        bar.appendChild(seg);
      });

      var labels = document.createElement('div');
      labels.className = 'ctv-timeline-labels';
      labels.innerHTML = '<span>0h</span><span>6h</span><span>12h</span><span>18h</span><span>24h CT</span>';

      wrap.appendChild(bar);
      wrap.appendChild(labels);
    };

    ui.renderBlockList = function (draft) {
      if (typeof ui.renderBlockTimeline === 'function') {
        ui.renderBlockTimeline(draft);
      }
      var list = document.getElementById('ctv-block-list');
      if (!list) return;
      list.innerHTML = '';

      var blocks = draft.blocks || [];

      blocks.forEach(function (block, bi) {
        var li = document.createElement('li');
        // Offline styling (Req 3.7)
        if (block.offline) {
          li.classList.add('ctv-block-offline');
        }

        // --- Header row ---
        var header = document.createElement('div');
        header.className = 'ctv-block-header';

        // Expand/collapse toggle
        var toggleBtn = document.createElement('button');
        toggleBtn.type = 'button';
        toggleBtn.className = 'raised btn-reorder';
        toggleBtn.textContent = '▶';
        toggleBtn.title = 'Expand / collapse';
        toggleBtn.style.minWidth = '2em';
        header.appendChild(toggleBtn);

        // Block title
        var titleSpan = document.createElement('span');
        titleSpan.className = 'ctv-block-header-title';
        titleSpan.textContent = block.name || '(unnamed block)';
        header.appendChild(titleSpan);

        // Hour range summary (Req 3.2)
        var hourSpan = document.createElement('span');
        hourSpan.style.fontSize = '0.8em';
        hourSpan.style.color = 'var(--text-color-secondary,#aaa)';
        hourSpan.textContent = block.offline
          ? '[Offline]'
          : (block.startHour !== undefined ? block.startHour : '?') +
            '\u2013' +
            (block.endHour !== undefined ? block.endHour : '?') + ' CT';
        header.appendChild(hourSpan);

        // Spacer
        var spacer = document.createElement('span');
        spacer.style.flex = '1';
        header.appendChild(spacer);

        // Move Up ▲ (Req 12.4)
        var upBtn = document.createElement('button');
        upBtn.type = 'button';
        upBtn.className = 'btn-reorder raised';
        upBtn.textContent = '▲';
        upBtn.title = 'Move block up';
        upBtn.disabled = (bi === 0);
        upBtn.addEventListener('click', function () {
          if (bi === 0) return;
          var tmp = draft.blocks[bi - 1];
          draft.blocks[bi - 1] = draft.blocks[bi];
          draft.blocks[bi] = tmp;
          ui.renderBlockList(draft);
        });
        header.appendChild(upBtn);

        // Move Down ▼ (Req 12.4)
        var downBtn = document.createElement('button');
        downBtn.type = 'button';
        downBtn.className = 'btn-reorder raised';
        downBtn.textContent = '▼';
        downBtn.title = 'Move block down';
        downBtn.disabled = (bi === blocks.length - 1);
        downBtn.addEventListener('click', function () {
          if (bi === blocks.length - 1) return;
          var tmp = draft.blocks[bi + 1];
          draft.blocks[bi + 1] = draft.blocks[bi];
          draft.blocks[bi] = tmp;
          ui.renderBlockList(draft);
        });
        header.appendChild(downBtn);

        // Duplicate block
        var dupBlockBtn = document.createElement('button');
        dupBlockBtn.type = 'button';
        dupBlockBtn.className = 'raised ctv-btn-xs';
        dupBlockBtn.textContent = 'Duplicate';
        dupBlockBtn.title = 'Duplicate block';
        dupBlockBtn.addEventListener('click', function () {
          var clone = JSON.parse(JSON.stringify(block));
          clone.name = (block.name || 'Block') + ' (Copy)';
          draft.blocks.splice(bi + 1, 0, clone);
          ui.renderBlockList(draft);
        });
        header.appendChild(dupBlockBtn);

        // Delete block (Req 3.8)
        var delBtn = document.createElement('button');
        delBtn.type = 'button';
        delBtn.className = 'raised button-cancel';
        delBtn.textContent = 'Delete';
        delBtn.style.fontSize = '0.8em';
        delBtn.addEventListener('click', function () {
          var blockName = block.name || ('Block ' + (bi + 1));
          _ctv.ui.showConfirm(
            'Delete block "' + blockName + '"? This cannot be undone.',
            function () {
              draft.blocks.splice(bi, 1);
              ui.renderBlockList(draft);
            }
          );
        });
        header.appendChild(delBtn);

        li.appendChild(header);

        // --- Collapsible body (inline edit form) ---
        var body = document.createElement('div');
        body.className = 'ctv-block-body';
        body.style.display = 'none'; // collapsed by default

        renderBlockForm(body, draft, bi);
        li.appendChild(body);

        // Wire expand/collapse toggle
        toggleBtn.addEventListener('click', function () {
          var expanded = body.style.display !== 'none';
          body.style.display = expanded ? 'none' : '';
          toggleBtn.textContent = expanded ? '▶' : '▼';
        });

        list.appendChild(li);
      });

      // Wire the "Add Block" button (idempotent — re-attach each render)
      var addBtn = document.getElementById('ctv-btn-add-block');
      if (addBtn) {
        // Remove previous listener by replacing the node clone
        var newAddBtn = addBtn.cloneNode(true);
        addBtn.parentNode.replaceChild(newAddBtn, addBtn);
        newAddBtn.addEventListener('click', function () {
          draft.blocks.push({
            name: '',
            startHour: 0,
            endHour: 24,
            offline: false,
            shows: []
          });
          ui.renderBlockList(draft);
          // Auto-expand the newly added block
          var items = list.querySelectorAll('li');
          var lastLi = items[items.length - 1];
          if (lastLi) {
            var lastBody = lastLi.querySelector('.ctv-block-body');
            var lastToggle = lastLi.querySelector('button[title="Expand / collapse"]');
            if (lastBody) lastBody.style.display = '';
            if (lastToggle) lastToggle.textContent = '▼';
          }
        });
      }
    };

    // -----------------------------------------------------------------------
    // Hook: when openChannelEditor calls renderBlockList (already called at
    // the bottom of openChannelEditor if this script is loaded). Also, call
    // renderBlockList now in case the editor is already open and the script
    // loaded late (defensive).
    // -----------------------------------------------------------------------
    if (_ctv._editorDraft && typeof ui.renderBlockList === 'function') {
      ui.renderBlockList(_ctv._editorDraft);
    }

  })();
  (function () {
    'use strict';

    var _ctv = window._ctv;
    var state = _ctv.state;
    var api   = _ctv.api;
    var ui    = _ctv.ui;

    // -----------------------------------------------------------------------
    // HTML-escape helper (local copy — this IIFE is self-contained)
    // -----------------------------------------------------------------------
    function _esc(str) {
      return String(str == null ? '' : str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
    }

    // -----------------------------------------------------------------------
    // Series picker state (per modal-open; not persisted across opens)
    // -----------------------------------------------------------------------
    var pickerState = {
      // Callback invoked when a confirmed show is ready to be added
      // Signature: function(show) — show is a full show object
      onConfirm: null,

      // Block index and draft reference for duplicate-check
      draft: null,
      bi: null,

      // Full series list from the server (for client-side filtering)
      seriesList: [],

      // Currently selected entries (multi-select across tabs)
      selectedMap: {},       // id -> { item, kind, episodeIds, episodeError, loading }
      selectedOrder: [],     // string[] preserving selection order
    };

    // -----------------------------------------------------------------------
    // ui.renderShowList — render show rows inside the ctv-show-section div
    // Req 4.1, 4.6, 12.1, 12.3
    //
    //   blockBody — the .ctv-block-body element
    //   draft     — the full channel draft object
    //   bi        — block index into draft.blocks
    // -----------------------------------------------------------------------
    ui.renderShowList = function (blockBody, draft, bi) {
      var section = blockBody.querySelector('.ctv-show-section');
      if (!section) return;
      section.innerHTML = '';

      var block = draft.blocks[bi];
      var shows = block.shows || [];

      // ---- Show list heading + Add Show button ----
      var headerRow = document.createElement('div');
      headerRow.style.cssText = 'display:flex;align-items:center;justify-content:space-between;margin-bottom:0.4em;';

      var heading = document.createElement('strong');
      heading.style.fontSize = '0.875em';
      heading.textContent = 'Shows (' + shows.length + ')';
      headerRow.appendChild(heading);

      var btnCluster = document.createElement('div');
      btnCluster.className = 'ctv-bulk-bar';

      if (shows.length > 0) {
        var shuffleAllBtn = document.createElement('button');
        shuffleAllBtn.type = 'button';
        shuffleAllBtn.className = 'raised ctv-btn-xs';
        shuffleAllBtn.textContent = 'Shuffle All';
        shuffleAllBtn.addEventListener('click', function () {
          shows.forEach(function (s) { s.shuffle = true; });
          ui.renderShowList(blockBody, draft, bi);
        });
        btnCluster.appendChild(shuffleAllBtn);

        var linearAllBtn = document.createElement('button');
        linearAllBtn.type = 'button';
        linearAllBtn.className = 'raised ctv-btn-xs';
        linearAllBtn.textContent = 'Linear All';
        linearAllBtn.addEventListener('click', function () {
          shows.forEach(function (s) { s.shuffle = false; });
          ui.renderShowList(blockBody, draft, bi);
        });
        btnCluster.appendChild(linearAllBtn);

        var sortBtn = document.createElement('button');
        sortBtn.type = 'button';
        sortBtn.className = 'raised ctv-btn-xs';
        sortBtn.textContent = 'Sort A–Z';
        sortBtn.addEventListener('click', function () {
          shows.sort(function (a, b) {
            return (a.name || '').localeCompare(b.name || '');
          });
          ui.renderShowList(blockBody, draft, bi);
        });
        btnCluster.appendChild(sortBtn);

        var clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.className = 'raised button-cancel ctv-btn-xs';
        clearBtn.textContent = 'Clear';
        clearBtn.addEventListener('click', function () {
          _ctv.ui.showConfirm('Remove all ' + shows.length + ' shows from this block?', function () {
            block.shows = [];
            ui.renderShowList(blockBody, draft, bi);
          });
        });
        btnCluster.appendChild(clearBtn);
      }

      var addBtn = document.createElement('button');
      addBtn.type = 'button';
      addBtn.className = 'raised button-submit';
      addBtn.style.fontSize = '0.8em';
      addBtn.textContent = '+ Add Show';
      addBtn.addEventListener('click', function () {
        openSeriesPicker(draft, bi, function (newShows) {
          var items = Array.isArray(newShows) ? newShows : [newShows];
          items.forEach(function (ns) { block.shows.push(ns); });
          ui.renderShowList(blockBody, draft, bi);
        });
      });
      btnCluster.appendChild(addBtn);
      headerRow.appendChild(btnCluster);
      section.appendChild(headerRow);

      if (shows.length === 0) {
        var empty = document.createElement('p');
        empty.style.cssText = 'font-size:0.85em;color:var(--text-color-secondary,#aaa);margin:0.25em 0;';
        empty.textContent = 'No shows assigned. Click "+ Add Show" to add a series.';
        section.appendChild(empty);
        return;
      }

      // ---- Show rows ----
      var ul = document.createElement('ul');
      ul.className = 'ctv-show-list';

      shows.forEach(function (show, si) {
        var li = document.createElement('li');

        // Show name + item count
        var nameSpan = document.createElement('span');
        nameSpan.className = 'ctv-show-name';
        var epCount = Array.isArray(show.jellyfinIds) ? show.jellyfinIds.length : 0;
        nameSpan.textContent = (show.name || '(unnamed)') +
          (epCount > 0 ? ' (' + epCount + (epCount === 1 ? ' item)' : ' eps)') : '');
        li.appendChild(nameSpan);

        // Weight label + input
        var weightLabel = document.createElement('label');
        weightLabel.style.cssText = 'font-size:0.8em;color:var(--text-color-secondary,#aaa);white-space:nowrap;';
        weightLabel.textContent = 'Weight:';
        li.appendChild(weightLabel);

        var weightInput = document.createElement('input');
        weightInput.type = 'number';
        weightInput.min = '1';
        weightInput.max = '100';
        weightInput.value = Number.isInteger(show.weight) ? show.weight : 1;
        weightInput.style.cssText = 'width:60px;background:rgba(255,255,255,0.07);' +
          'color:var(--theme-text-color,#fff);border:1px solid var(--divider-color,#444);' +
          'border-radius:3px;padding:0.3em 0.4em;font-size:0.9em;box-sizing:border-box;';
        weightInput.addEventListener('input', function () {
          var v = parseInt(weightInput.value, 10);
          show.weight = isNaN(v) ? weightInput.value : v;
        });
        li.appendChild(weightInput);

        // Order label + select
        var orderLabel = document.createElement('label');
        orderLabel.style.cssText = 'font-size:0.8em;color:var(--text-color-secondary,#aaa);white-space:nowrap;';
        orderLabel.textContent = 'Order:';
        li.appendChild(orderLabel);

        var orderSelect = document.createElement('select');
        orderSelect.style.cssText = 'background:rgba(255,255,255,0.07);color:var(--theme-text-color,#fff);' +
          'border:1px solid var(--divider-color,#444);border-radius:3px;' +
          'padding:0.3em 0.4em;font-size:0.9em;';

        var linearOpt = document.createElement('option');
        linearOpt.value = 'linear';
        linearOpt.textContent = 'Linear';
        orderSelect.appendChild(linearOpt);

        var shuffleOpt = document.createElement('option');
        shuffleOpt.value = 'shuffle';
        shuffleOpt.textContent = 'Shuffle';
        orderSelect.appendChild(shuffleOpt);

        orderSelect.value = show.shuffle ? 'shuffle' : 'linear';
        orderSelect.addEventListener('change', function () {
          show.shuffle = orderSelect.value === 'shuffle';
        });
        li.appendChild(orderSelect);

        // Move Up ▲ (Req 12.3)
        var upBtn = document.createElement('button');
        upBtn.type = 'button';
        upBtn.className = 'btn-reorder raised';
        upBtn.textContent = '▲';
        upBtn.title = 'Move show up';
        upBtn.disabled = (si === 0);
        upBtn.addEventListener('click', function () {
          if (si === 0) return;
          var tmp = block.shows[si - 1];
          block.shows[si - 1] = block.shows[si];
          block.shows[si] = tmp;
          ui.renderShowList(blockBody, draft, bi);
        });
        li.appendChild(upBtn);

        // Move Down ▼ (Req 12.3)
        var downBtn = document.createElement('button');
        downBtn.type = 'button';
        downBtn.className = 'btn-reorder raised';
        downBtn.textContent = '▼';
        downBtn.title = 'Move show down';
        downBtn.disabled = (si === shows.length - 1);
        downBtn.addEventListener('click', function () {
          if (si === shows.length - 1) return;
          var tmp = block.shows[si + 1];
          block.shows[si + 1] = block.shows[si];
          block.shows[si] = tmp;
          ui.renderShowList(blockBody, draft, bi);
        });
        li.appendChild(downBtn);

        // Remove button (Req 4.9)
        var removeBtn = document.createElement('button');
        removeBtn.type = 'button';
        removeBtn.className = 'raised button-cancel';
        removeBtn.style.fontSize = '0.8em';
        removeBtn.textContent = 'Remove';
        removeBtn.addEventListener('click', function () {
          block.shows.splice(si, 1);
          ui.renderShowList(blockBody, draft, bi);
        });
        li.appendChild(removeBtn);

        ul.appendChild(li);
      });

      section.appendChild(ul);
    };

    // -----------------------------------------------------------------------
    // openSeriesPicker — open the series modal for a given block
    //
    //   draft      — full channel draft
    //   bi         — block index
    //   onConfirm  — callback(show) called when user confirms a selection
    // -----------------------------------------------------------------------
    function openSeriesPicker(draft, bi, onConfirm) {
      // Stash context for use inside the picker
      pickerState.onConfirm = onConfirm;
      pickerState.draft = draft;
      pickerState.bi = bi;
      pickerState.mode = 'series';
      pickerState.selectedMap = {};
      pickerState.selectedOrder = [];
      pickerState.moviesList = state.moviesCache || [];

      renderPickerShell();

      // Show the overlay
      document.getElementById('ctv-series-overlay').style.display = '';

      loadPickerTab('series');
    }

    function loadPickerTab(mode) {
      pickerState.mode = mode;

      var tabSeries = document.getElementById('ctv-picker-tab-series');
      var tabMovies = document.getElementById('ctv-picker-tab-movies');
      if (tabSeries && tabMovies) {
        tabSeries.classList.toggle('button-submit', mode === 'series');
        tabMovies.classList.toggle('button-submit', mode === 'movies');
      }
      var search = document.getElementById('ctv-series-search');
      if (search) {
        search.value = '';
        search.placeholder = mode === 'movies' ? 'Filter movies…' : 'Filter series…';
      }
      pickerState.genre = '';
      clearPickerError();
      updatePickerFooter();

      if (mode === 'movies') {
        if (state.moviesCache) {
          pickerState.moviesList = state.moviesCache;
          populateGenreFilter(state.moviesCache);
          renderSeriesList(state.moviesCache, '');
        } else {
          api.loadMovies()
            .then(function (list) {
              list = (list || []).slice().sort(function (a, b) {
                return (a.name || '').localeCompare(b.name || '');
              });
              state.moviesCache = list;
              pickerState.moviesList = list;
              if (pickerState.mode === 'movies') {
                populateGenreFilter(list);
                var curQ = (document.getElementById('ctv-series-search') || {}).value || '';
                renderSeriesList(list, curQ);
              }
            })
            .catch(function (err) {
              console.error('ClassicTV: loadMovies error', err);
              showPickerError('Failed to load movies from the Jellyfin library. Please try again.');
            });
        }
        return;
      }

      // Load series — exactly one request per modal-open (Req 4.3).
      // Re-opens in the same page session may reuse state.seriesCache (Req 4.3 note).
      if (state.seriesCache) {
        pickerState.seriesList = state.seriesCache;
        populateGenreFilter(state.seriesCache);
        renderSeriesList(state.seriesCache, '');
      } else {
        api.loadSeries()
          .then(function (data) {
            // data is SeriesListResponse: { series: [...] }
            var list = (data && data.series) ? data.series : (Array.isArray(data) ? data : []);
            // Sort by name
            list = list.slice().sort(function (a, b) {
              return (a.name || '').localeCompare(b.name || '');
            });
            state.seriesCache = list;
            pickerState.seriesList = list;
            if (pickerState.mode === 'series') {
              populateGenreFilter(list);
              var curQ = (document.getElementById('ctv-series-search') || {}).value || '';
              renderSeriesList(list, curQ);
            }
          })
          .catch(function (err) {
            console.error('ClassicTV: loadSeries error', err);
            showPickerError('Failed to load series from the Jellyfin library. Please try again.');
          });
      }
    }

    // -----------------------------------------------------------------------
    // renderPickerShell — build the modal HTML skeleton
    // -----------------------------------------------------------------------
    function renderPickerShell() {
      var content = document.getElementById('ctv-series-content');
      content.innerHTML = [
        '<h2 style="margin-top:0;">Add Show or Movie</h2>',

        // Mode tabs: TV Series vs Movies + quick bulk select
        '<div style="display:flex;align-items:center;justify-content:space-between;gap:0.5em;margin-bottom:0.75em;flex-wrap:wrap;">',
        '  <div style="display:flex;gap:0.5em;">',
        '    <button id="ctv-picker-tab-series" type="button" class="raised button-submit">TV Series</button>',
        '    <button id="ctv-picker-tab-movies" type="button" class="raised">Movies</button>',
        '  </div>',
        '  <div style="display:flex;gap:0.4em;">',
        '    <button id="ctv-picker-select-all" type="button" class="raised ctv-btn-xs">Select All Visible</button>',
        '    <button id="ctv-picker-clear-all" type="button" class="raised ctv-btn-xs">Clear</button>',
        '  </div>',
        '</div>',

        // Error area (hidden by default)
        '<div id="ctv-picker-error" class="ctv-error" style="display:none;margin-bottom:0.5em;"></div>',

        // Search + Genre filter row
        '<div style="display:flex;gap:0.5em;margin-bottom:0.5em;flex-wrap:wrap;align-items:center;">',
        '  <input id="ctv-series-search" type="text" placeholder="Filter series…" autocomplete="off" style="flex:1;min-width:160px;margin-bottom:0;">',
        '  <select id="ctv-picker-genre" style="background:rgba(255,255,255,0.07);color:var(--theme-text-color,#fff);border:1px solid var(--divider-color,#444);border-radius:3px;padding:0.4em 0.6em;font-size:0.9em;max-width:180px;">',
        '    <option value="">All Genres</option>',
        '  </select>',
        (pickerState.draft && pickerState.draft.maxRating
          ? '  <label style="display:inline-flex;align-items:center;gap:0.3em;font-size:0.85em;cursor:pointer;white-space:nowrap;"><input id="ctv-picker-rating-filter" type="checkbox" checked> Max ' + _esc(pickerState.draft.maxRating) + '</label>'
          : ''),
        '</div>',

        // Series / Movies list (filled by renderSeriesList)
        '<ul id="ctv-series-list"></ul>',

        // Episode / Movie preview area
        '<div id="ctv-picker-ep-preview" style="min-height:1.4em;font-size:0.85em;' +
          'color:var(--text-color-secondary,#aaa);margin-top:0.4em;"></div>',

        '<hr class="ctv-sep">',

        // Footer actions
        '<div style="display:flex;gap:0.5em;flex-wrap:wrap;">',
        '  <button id="ctv-picker-btn-add" type="button" class="raised button-submit" disabled>Add</button>',
        '  <button id="ctv-picker-btn-cancel" type="button" class="raised">Cancel</button>',
        '</div>'
      ].join('\n');

      // Wire tabs
      document.getElementById('ctv-picker-tab-series').addEventListener('click', function () {
        if (pickerState.mode !== 'series') { loadPickerTab('series'); }
      });
      document.getElementById('ctv-picker-tab-movies').addEventListener('click', function () {
        if (pickerState.mode !== 'movies') { loadPickerTab('movies'); }
      });

      // Wire cancel button
      document.getElementById('ctv-picker-btn-cancel').addEventListener('click', function () {
        closeSeriesPicker();
      });

      // Wire Add (confirm) button
      document.getElementById('ctv-picker-btn-add').addEventListener('click', function () {
        confirmSeriesSelection();
      });

      // Wire search filter
      document.getElementById('ctv-series-search').addEventListener('input', function () {
        var q = this.value;
        var activeList = pickerState.mode === 'movies' ? pickerState.moviesList : pickerState.seriesList;
        renderSeriesList(activeList, q);
      });

      // Wire genre filter
      document.getElementById('ctv-picker-genre').addEventListener('change', function () {
        pickerState.genre = this.value;
        var q = (document.getElementById('ctv-series-search') || {}).value || '';
        var activeList = pickerState.mode === 'movies' ? pickerState.moviesList : pickerState.seriesList;
        renderSeriesList(activeList, q);
      });

      var ratingFilterCb = document.getElementById('ctv-picker-rating-filter');
      if (ratingFilterCb) {
        ratingFilterCb.addEventListener('change', function () {
          var q = (document.getElementById('ctv-series-search') || {}).value || '';
          var activeList = pickerState.mode === 'movies' ? pickerState.moviesList : pickerState.seriesList;
          renderSeriesList(activeList, q);
        });
      }

      // Wire Select All Visible / Clear
      document.getElementById('ctv-picker-select-all').addEventListener('click', function () {
        var q = (document.getElementById('ctv-series-search') || {}).value || '';
        var activeList = pickerState.mode === 'movies' ? pickerState.moviesList : pickerState.seriesList;
        var isMovies = pickerState.mode === 'movies';
        var visible = getFilteredPickerItems(activeList, q, pickerState.genre);
        visible.forEach(function (item) {
          if (!isItemInBlock(item.id) && !pickerState.selectedMap[item.id]) {
            if (isMovies) { toggleMovie(item); } else { toggleSeries(item); }
          }
        });
      });

      document.getElementById('ctv-picker-clear-all').addEventListener('click', function () {
        pickerState.selectedMap = {};
        pickerState.selectedOrder = [];
        var q = (document.getElementById('ctv-series-search') || {}).value || '';
        var activeList = pickerState.mode === 'movies' ? pickerState.moviesList : pickerState.seriesList;
        renderSeriesList(activeList, q);
        updatePickerFooter();
      });
    }

    function populateGenreFilter(list) {
      var select = document.getElementById('ctv-picker-genre');
      if (!select) return;
      var genreSet = {};
      (list || []).forEach(function (item) {
        (item.genres || []).forEach(function (g) {
          if (g) { genreSet[g] = true; }
        });
      });
      var genres = Object.keys(genreSet).sort(function (a, b) {
        return a.localeCompare(b);
      });
      select.innerHTML = '<option value="">All Genres (' + genres.length + ')</option>';
      genres.forEach(function (g) {
        var opt = document.createElement('option');
        opt.value = g;
        opt.textContent = g;
        select.appendChild(opt);
      });
      select.value = pickerState.genre || '';
    }

    function getRatingRank(rating) {
      if (!rating) return null;
      var norm = String(rating).trim().toUpperCase();
      if (norm === 'G' || norm === 'TV-G' || norm === 'TV-Y' || norm === 'TV-Y7' || norm === 'TV-Y7-FV') return 1;
      if (norm === 'PG' || norm === 'TV-PG' || norm === 'GP' || norm === 'PASSED' || norm === 'APPROVED') return 2;
      if (norm === 'PG-13' || norm === 'PG13' || norm === 'TV-14' || norm === 'M') return 3;
      if (norm === 'R' || norm === 'TV-MA') return 4;
      if (norm === 'NC-17' || norm === 'NC17' || norm === 'X') return 5;
      return null;
    }

    function isRatingAllowedForDraft(itemRating, draft) {
      if (!draft || !draft.maxRating) return true;
      var maxRank = getRatingRank(draft.maxRating);
      if (maxRank === null) return true;
      var itemRank = getRatingRank(itemRating);
      if (itemRank === null) return draft.allowUnrated !== false;
      return itemRank <= maxRank;
    }

    function getFilteredPickerItems(list, query, genre) {
      var isMovies = pickerState.mode === 'movies';
      var lower = (query || '').trim().toLowerCase();
      var selectedGenre = genre || '';
      var hideOver = !!(document.getElementById('ctv-picker-rating-filter') &&
        document.getElementById('ctv-picker-rating-filter').checked);
      return (list || []).filter(function (s) {
        if (hideOver && !isRatingAllowedForDraft(s.officialRating, pickerState.draft)) {
          return false;
        }
        if (selectedGenre) {
          var itemGenres = s.genres || [];
          if (itemGenres.indexOf(selectedGenre) === -1) { return false; }
        }
        if (!lower) { return true; }
        var nameMatch = (s.name || '').toLowerCase().indexOf(lower) !== -1;
        var yearMatch = isMovies && s.year && String(s.year).indexOf(lower) !== -1;
        return nameMatch || yearMatch;
      });
    }

    // -----------------------------------------------------------------------
    // renderSeriesList — populate #ctv-series-list from a filtered set
    // -----------------------------------------------------------------------
    function renderSeriesList(list, query) {
      var ul = document.getElementById('ctv-series-list');
      if (!ul) return;
      ul.innerHTML = '';

      var isMovies = pickerState.mode === 'movies';
      var filtered = getFilteredPickerItems(list, query, pickerState.genre);

      if (filtered.length === 0) {
        var empty = document.createElement('li');
        empty.style.cssText = 'color:var(--text-color-secondary,#aaa);cursor:default;';
        var noun = isMovies ? 'movies' : 'series';
        empty.textContent = (query || pickerState.genre) ? 'No ' + noun + ' match the current filter.' : 'No ' + noun + ' found in library.';
        ul.appendChild(empty);
        return;
      }

      filtered.forEach(function (item) {
        var li = document.createElement('li');
        var inBlock = isItemInBlock(item.id);
        var isSelected = !!pickerState.selectedMap[item.id];

        if (isSelected) {
          li.classList.add('selected');
        }
        if (inBlock) {
          li.style.opacity = '0.5';
          li.style.cursor = 'default';
        }

        var leftWrap = document.createElement('span');
        leftWrap.style.cssText = 'display:flex;align-items:center;gap:0.5em;min-width:0;';

        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = isSelected || inBlock;
        cb.disabled = inBlock;
        cb.style.pointerEvents = 'none';
        leftWrap.appendChild(cb);

        var nameSpan = document.createElement('span');
        nameSpan.textContent = item.name || '(unnamed)';
        leftWrap.appendChild(nameSpan);
        li.appendChild(leftWrap);

        var metaSpan = document.createElement('span');
        metaSpan.className = 'ctv-series-ep-count';
        var genreText = (item.genres && item.genres.length > 0) ? item.genres.slice(0, 2).join(', ') : '';
        var ratingText = item.officialRating || 'Unrated';
        var overRating = !isRatingAllowedForDraft(item.officialRating, pickerState.draft);
        if (overRating && !inBlock) {
          li.style.opacity = '0.6';
        }
        if (inBlock) {
          metaSpan.textContent = 'Already in block';
        } else if (isMovies) {
          var mins = item.durationSeconds > 0 ? Math.round(item.durationSeconds / 60) + 'm' : '';
          var parts = [];
          if (item.year) { parts.push(String(item.year)); }
          if (mins) { parts.push(mins); }
          parts.push(ratingText);
          if (genreText) { parts.push(genreText); }
          metaSpan.textContent = parts.join(' · ') || 'Movie';
        } else {
          var sParts = [item.episodeCount + ' episodes', ratingText];
          if (genreText) { sParts.push(genreText); }
          metaSpan.textContent = sParts.join(' · ');
        }
        li.appendChild(metaSpan);

        if (!inBlock) {
          li.addEventListener('click', function () {
            if (isMovies) {
              toggleMovie(item);
            } else {
              toggleSeries(item);
            }
          });
        }

        ul.appendChild(li);
      });
    }

    function isItemInBlock(itemId) {
      if (!pickerState.draft || pickerState.bi === null || pickerState.bi === undefined) {
        return false;
      }
      var block = pickerState.draft.blocks[pickerState.bi];
      return (block.shows || []).some(function (sh) {
        return sh._seriesId === itemId ||
          (Array.isArray(sh.jellyfinIds) && sh.jellyfinIds.length === 1 && sh.jellyfinIds[0] === itemId);
      });
    }

    function updatePickerFooter() {
      var order = pickerState.selectedOrder;
      var total = order.length;
      var loadingCount = 0;
      var errorMsg = null;
      var totalEpisodes = 0;
      var movieCount = 0;
      var seriesCount = 0;

      order.forEach(function (id) {
        var entry = pickerState.selectedMap[id];
        if (!entry) return;
        if (entry.kind === 'movie') { movieCount += 1; } else { seriesCount += 1; }
        if (entry.loading) {
          loadingCount += 1;
        } else if (entry.episodeError) {
          errorMsg = errorMsg || entry.episodeError;
        } else if (!entry.episodeIds || entry.episodeIds.length === 0) {
          errorMsg = errorMsg || ('"' + (entry.item.name || id) + '" has no episodes in the library.');
        } else {
          totalEpisodes += entry.episodeIds.length;
        }
      });

      var preview = document.getElementById('ctv-picker-ep-preview');
      if (preview) {
        if (total === 0) {
          preview.textContent = '';
        } else if (errorMsg) {
          preview.innerHTML = '<span class="ctv-error">' + _esc(errorMsg) + '</span>';
        } else if (loadingCount > 0) {
          preview.textContent = total + ' selected · loading episodes…';
        } else {
          var parts = [];
          if (seriesCount > 0) { parts.push(seriesCount + ' series'); }
          if (movieCount > 0) { parts.push(movieCount + ' movie' + (movieCount !== 1 ? 's' : '')); }
          preview.innerHTML = '';
          var summarySpan = document.createElement('div');
          summarySpan.textContent = total + ' selected (' + parts.join(', ') + ' — ' + totalEpisodes + ' item' + (totalEpisodes !== 1 ? 's' : '') + ')';
          preview.appendChild(summarySpan);

          if (total === 1 && seriesCount === 1) {
            var singleEntry = pickerState.selectedMap[order[0]];
            if (singleEntry && Array.isArray(singleEntry.episodes) && singleEntry.episodes.length > 0) {
              var seasonCounts = {};
              var seasons = [];
              singleEntry.episodes.forEach(function (ep) {
                var sNum = ep.season != null ? ep.season : 0;
                if (!seasonCounts[sNum]) {
                  seasonCounts[sNum] = 0;
                  seasons.push(sNum);
                }
                seasonCounts[sNum] += 1;
              });
              seasons.sort(function (a, b) { return a - b; });
              if (seasons.length > 1) {
                var sBar = document.createElement('div');
                sBar.className = 'ctv-season-bar';
                var sTitle = document.createElement('span');
                sTitle.textContent = 'Seasons:';
                sBar.appendChild(sTitle);
                seasons.forEach(function (sNum) {
                  var lbl = document.createElement('label');
                  var scb = document.createElement('input');
                  scb.type = 'checkbox';
                  scb.checked = !singleEntry.selectedSeasons || !!singleEntry.selectedSeasons[sNum];
                  scb.addEventListener('change', function () {
                    if (!singleEntry.selectedSeasons) {
                      singleEntry.selectedSeasons = {};
                      seasons.forEach(function (sn) { singleEntry.selectedSeasons[sn] = true; });
                    }
                    singleEntry.selectedSeasons[sNum] = scb.checked;
                    singleEntry.episodeIds = singleEntry.episodes
                      .filter(function (ep) { return singleEntry.selectedSeasons[ep.season != null ? ep.season : 0]; })
                      .map(function (ep) { return ep.id; });
                    updatePickerFooter();
                  });
                  lbl.appendChild(scb);
                  lbl.appendChild(document.createTextNode((sNum === 0 ? 'Specials' : 'S' + sNum) + ' (' + seasonCounts[sNum] + ')'));
                  sBar.appendChild(lbl);
                });
                preview.appendChild(sBar);
              }
            }
          }
        }
      }

      var addBtn = document.getElementById('ctv-picker-btn-add');
      if (addBtn) {
        addBtn.textContent = total > 0 ? 'Add (' + total + ')' : 'Add';
        addBtn.disabled = total === 0 || loadingCount > 0;
      }
    }

    function toggleMovie(movie) {
      clearPickerError();
      if (pickerState.selectedMap[movie.id]) {
        delete pickerState.selectedMap[movie.id];
        pickerState.selectedOrder = pickerState.selectedOrder.filter(function (id) { return id !== movie.id; });
      } else {
        pickerState.selectedMap[movie.id] = {
          item: movie,
          kind: 'movie',
          episodeIds: [movie.id],
          episodeError: null,
          loading: false
        };
        pickerState.selectedOrder.push(movie.id);
      }

      var q = (document.getElementById('ctv-series-search') || {}).value || '';
      renderSeriesList(pickerState.moviesList, q);
      updatePickerFooter();
    }

    // -----------------------------------------------------------------------
    // toggleSeries — toggle a series row in multi-select and fetch its episodes
    // -----------------------------------------------------------------------
    function toggleSeries(series) {
      clearPickerError();
      if (pickerState.selectedMap[series.id]) {
        delete pickerState.selectedMap[series.id];
        pickerState.selectedOrder = pickerState.selectedOrder.filter(function (id) { return id !== series.id; });
        var qRem = (document.getElementById('ctv-series-search') || {}).value || '';
        renderSeriesList(pickerState.seriesList, qRem);
        updatePickerFooter();
        return;
      }

      pickerState.selectedMap[series.id] = {
        item: series,
        kind: 'series',
        episodeIds: null,
        episodeError: null,
        loading: true
      };
      pickerState.selectedOrder.push(series.id);

      var q = (document.getElementById('ctv-series-search') || {}).value || '';
      renderSeriesList(pickerState.seriesList, q);
      updatePickerFooter();

      // Fetch episodes
      api.loadEpisodes(series.id)
        .then(function (data) {
          var entry = pickerState.selectedMap[series.id];
          if (!entry) return; // unselected while loading
          var episodes = (data && data.episodes) ? data.episodes : (Array.isArray(data) ? data : []);
          entry.episodes = episodes;
          entry.selectedSeasons = null;
          entry.episodeIds = episodes.map(function (ep) { return ep.id; });
          entry.episodeError = null;
          entry.loading = false;
          updatePickerFooter();
        })
        .catch(function (err) {
          console.error('ClassicTV: loadEpisodes error', err);
          var entry = pickerState.selectedMap[series.id];
          if (!entry) return;
          entry.episodeIds = null;
          entry.episodeError = 'Failed to load episodes for "' + (series.name || series.id) + '".';
          entry.loading = false;
          updatePickerFooter();
        });
    }

    // -----------------------------------------------------------------------
    // confirmSeriesSelection — called when user clicks "Add (N)"
    // -----------------------------------------------------------------------
    function confirmSeriesSelection() {
      var order = pickerState.selectedOrder;
      if (order.length === 0) {
        showPickerError('Please select at least one show or movie.');
        return;
      }

      var newShows = [];
      for (var i = 0; i < order.length; i += 1) {
        var entry = pickerState.selectedMap[order[i]];
        if (!entry) continue;
        if (entry.loading || entry.episodeError || entry.episodeIds === null) {
          showPickerError(entry.episodeError || 'Episodes are still loading. Please wait.');
          return;
        }
        if (entry.episodeIds.length === 0) {
          showPickerError('"' + _esc(entry.item.name) + '" has no episodes in the library.');
          return;
        }
        if (isItemInBlock(entry.item.id)) {
          showPickerError('"' + _esc(entry.item.name) + '" is already in this block.');
          return;
        }
        newShows.push({
          name: entry.item.name,
          weight: 1,
          shuffle: false,
          jellyfinIds: entry.episodeIds.slice(),
          _seriesId: entry.item.id   // transient; stripped before POST by task 7.5
        });
      }

      // Capture the callback before closing — closeSeriesPicker() resets
      // pickerState.onConfirm to null, so reading it afterwards always missed.
      var onConfirm = pickerState.onConfirm;
      closeSeriesPicker();
      if (typeof onConfirm === 'function') {
        onConfirm(newShows);
      }
    }

    // -----------------------------------------------------------------------
    // closeSeriesPicker
    // -----------------------------------------------------------------------
    function closeSeriesPicker() {
      document.getElementById('ctv-series-overlay').style.display = 'none';
      // Reset transient picker state (but keep seriesList for re-use by Req 4.3 note)
      pickerState.selectedMap   = {};
      pickerState.selectedOrder = [];
      pickerState.onConfirm     = null;
    }

    // -----------------------------------------------------------------------
    // showPickerError — display an error inside the series picker modal
    // Req 4.3 error path, 4.4 error path
    // -----------------------------------------------------------------------
    function showPickerError(message) {
      var errDiv = document.getElementById('ctv-picker-error');
      if (!errDiv) return;
      errDiv.textContent = message;
      errDiv.style.display = '';
    }

    function clearPickerError() {
      var errDiv = document.getElementById('ctv-picker-error');
      if (!errDiv) return;
      errDiv.textContent = '';
      errDiv.style.display = 'none';
    }

    // -----------------------------------------------------------------------
    // Patch renderBlockForm — replace the task 7.6 placeholder with a real
    // show section backed by ui.renderShowList.
    //
    // Strategy: monkey-patch the existing renderBlockList so that after it
    // rebuilds the block DOM, every block body's show-section is populated
    // by ui.renderShowList. We also need the blockBody reference to call
    // renderShowList; we achieve this by post-processing all block bodies
    // after the original renderBlockList renders them.
    //
    // Additionally, override the original placeholder logic: when the editor
    // opens a block, renderBlockList is called and then for each expanded body
    // we call renderShowList. The placeholder <em> will still exist in
    // renderBlockForm output initially, but renderShowList replaces innerHTML
    // so it takes over immediately on expand.
    //
    // To ensure shows render even when a block is expanded by the toggle,
    // we intercept the expand toggle click. Since toggle buttons are created
    // fresh each renderBlockList call, we hook into renderBlockList.
    // -----------------------------------------------------------------------
    var _originalRenderBlockList = ui.renderBlockList;

    ui.renderBlockList = function (draft) {
      // Run the original (7.6) render
      _originalRenderBlockList.call(ui, draft);

      // After render, for every block body that's currently visible (display !== 'none'),
      // populate the show section. Also populate hidden bodies so data is ready when expanded.
      var list = document.getElementById('ctv-block-list');
      if (!list) return;

      var items = list.querySelectorAll('li');
      items.forEach(function (li, bi) {
        var body = li.querySelector('.ctv-block-body');
        if (!body) return;
        var section = body.querySelector('.ctv-show-section');
        if (!section) return;
        ui.renderShowList(body, draft, bi);

        // Re-wire the expand toggle to also call renderShowList on open,
        // ensuring shows are always fresh when the block is expanded.
        var toggleBtn = li.querySelector('button[title="Expand / collapse"]');
        if (toggleBtn) {
          // Clone to drop original listeners, then re-attach
          var newToggle = toggleBtn.cloneNode(true);
          toggleBtn.parentNode.replaceChild(newToggle, toggleBtn);
          newToggle.addEventListener('click', function () {
            var expanded = body.style.display !== 'none';
            body.style.display = expanded ? 'none' : '';
            newToggle.textContent = expanded ? '▶' : '▼';
            if (!expanded) {
              // Block just became visible — refresh show list
              ui.renderShowList(body, draft, bi);
            }
          });
        }
      });
    };

    // -----------------------------------------------------------------------
    // If the editor is already open (e.g., this script loaded after an editor
    // open event), re-render the block list so show sections appear.
    // -----------------------------------------------------------------------
    if (_ctv._editorDraft) {
      ui.renderBlockList(_ctv._editorDraft);
    }

    // Expose closeSeries picker for external use if needed
    _ctv.closeSeriesPicker = closeSeriesPicker;

  })();
  (function () {
    'use strict';

    var _ctv = window._ctv;
    var ui   = _ctv.ui;

    // -----------------------------------------------------------------------
    // HTML-escape helper (local copy)
    // -----------------------------------------------------------------------
    function _esc(str) {
      return String(str == null ? '' : str)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;');
    }

    // -----------------------------------------------------------------------
    // ui.showFullPageError(message, onRetry)                    (Req 10.6)
    //
    // Replaces #ctv-channel-list content with an error message and optional
    // Retry button. Hides the "New Channel" button and the error banner while
    // the full-page error is displayed so the page is never in a blank or
    // partial state.
    //
    // When onRetry is provided, clicking Retry calls onRetry().  If onRetry
    // returns a Promise and it resolves, the full-page error is removed and
    // the normal page is restored.  If it rejects (or onRetry is a plain
    // function that throws), the error is left in place.
    //
    // When onRetry is null, no Retry button is rendered.
    // -----------------------------------------------------------------------
    ui.showFullPageError = function (message, onRetry) {
      var list        = document.getElementById('ctv-channel-list');
      var banner      = document.getElementById('ctv-error-banner');
      var newChBtn    = document.getElementById('ctv-btn-new-channel');

      // Hide the new-channel button and the separate error banner so only the
      // full-page error is visible — no duplicate messaging, no partial UI.
      if (newChBtn)  { newChBtn.style.display = 'none'; }
      if (banner)    { banner.style.display   = 'none'; }

      // Build the error container
      var wrapper = document.createElement('div');
      wrapper.id = 'ctv-fullpage-error';

      var msgEl = document.createElement('p');
      msgEl.className = 'ctv-error';
      msgEl.style.fontSize = '1em';
      msgEl.textContent = message;
      wrapper.appendChild(msgEl);

      if (typeof onRetry === 'function') {
        var retryBtn = document.createElement('button');
        retryBtn.className = 'raised button-submit';
        retryBtn.textContent = 'Retry';
        retryBtn.addEventListener('click', function () {
          // Disable the button while the retry is in flight
          retryBtn.disabled = true;
          retryBtn.textContent = 'Retrying…';

          try {
            var result = onRetry();
            // If onRetry returned a Promise, wait for resolution
            if (result && typeof result.then === 'function') {
              result.then(function () {
                // Retry succeeded — restore the page chrome
                if (newChBtn) { newChBtn.style.display = ''; }
                // The caller is responsible for re-populating #ctv-channel-list
                // (e.g. via ui.renderChannelList); remove only the error wrapper.
                if (wrapper.parentNode) { wrapper.parentNode.removeChild(wrapper); }
              }).catch(function () {
                // Retry failed — re-enable so the user can try again
                retryBtn.disabled = false;
                retryBtn.textContent = 'Retry';
              });
            } else {
              // Synchronous onRetry — restore immediately
              if (newChBtn) { newChBtn.style.display = ''; }
              if (wrapper.parentNode) { wrapper.parentNode.removeChild(wrapper); }
            }
          } catch (e) {
            retryBtn.disabled = false;
            retryBtn.textContent = 'Retry';
          }
        });
        wrapper.appendChild(retryBtn);
      }

      list.innerHTML = '';
      list.appendChild(wrapper);
    };

    // -----------------------------------------------------------------------
    // ui.showConfirm(message, onConfirm)                  (Req 1.8, 2.11)
    //
    // Renders an in-page confirm box — NOT window.confirm — so it works inside
    // the Jellyfin dashboard iframe context.  The box is appended to
    // #ctv-channel-list (visible from both the list view and editor context).
    //
    // "Confirm" calls onConfirm() then removes the box.
    // "Cancel" removes the box without calling onConfirm().
    //
    // Only one confirm box is shown at a time; calling showConfirm while one
    // is open removes the previous one first.
    // -----------------------------------------------------------------------
    ui.showConfirm = function (message, onConfirm) {
      // Remove any existing confirm box first (idempotent)
      var existing = document.getElementById('ctv-confirm-box');
      if (existing && existing.parentNode) {
        existing.parentNode.removeChild(existing);
      }

      var box = document.createElement('div');
      box.id = 'ctv-confirm-box';
      box.className = 'ctv-confirm-box';

      var msgEl = document.createElement('p');
      msgEl.style.margin = '0 0 0.5em';
      msgEl.textContent = message;
      box.appendChild(msgEl);

      var actions = document.createElement('div');
      actions.className = 'ctv-confirm-box-actions';

      var confirmBtn = document.createElement('button');
      confirmBtn.type = 'button';
      confirmBtn.className = 'raised button-submit';
      confirmBtn.textContent = 'Confirm';
      confirmBtn.addEventListener('click', function () {
        if (box.parentNode) { box.parentNode.removeChild(box); }
        if (typeof onConfirm === 'function') { onConfirm(); }
      });
      actions.appendChild(confirmBtn);

      var cancelBtn = document.createElement('button');
      cancelBtn.type = 'button';
      cancelBtn.className = 'raised';
      cancelBtn.textContent = 'Cancel';
      cancelBtn.addEventListener('click', function () {
        if (box.parentNode) { box.parentNode.removeChild(box); }
      });
      actions.appendChild(cancelBtn);

      box.appendChild(actions);

      // Position fixed above the editor modal (z-index 1000). The editor modal
      // covers the page, so a confirm box inserted into #ctv-channel-list was
      // hidden behind it and a Delete click looked like it did nothing.
      box.style.position = 'fixed';
      box.style.top = '50%';
      box.style.left = '50%';
      box.style.transform = 'translate(-50%, -50%)';
      box.style.zIndex = '2000';
      box.style.minWidth = '18em';
      box.style.maxWidth = '90vw';
      box.style.background = 'var(--theme-body-background-color, #1c1c1c)';
      box.style.boxShadow = '0 8px 30px rgba(0, 0, 0, 0.6)';

      var container = document.getElementById('ClassicTVConfigPage') ||
                      document.getElementById('ctv-channel-list') ||
                      document.body;
      container.appendChild(box);
    };

    // -----------------------------------------------------------------------
    // ui.showWarning(message)                             (Req 1.7, 2.7)
    //
    // Shows a non-blocking warning in #ctv-error-banner with yellow styling.
    // Auto-clears after 5 seconds.
    //
    // Calling showWarning again while one is visible resets the timer and
    // updates the message so overlapping warnings don't stack.
    // -----------------------------------------------------------------------

    // Module-level timer handle so we can clear it on re-entry
    var _warningTimer = null;

    ui.showWarning = function (message) {
      var banner = document.getElementById('ctv-error-banner');
      if (!banner) { return; }

      // Override banner styles for warning (yellow) rather than error (red)
      banner.style.background    = 'rgba(249, 168, 37, 0.12)';
      banner.style.border        = '1px solid rgba(249, 168, 37, 0.4)';
      banner.style.color         = 'var(--color-yellow, #f9a825)';
      banner.textContent         = message;
      banner.style.display       = '';

      // Reset the auto-hide timer
      if (_warningTimer !== null) {
        clearTimeout(_warningTimer);
      }
      _warningTimer = setTimeout(function () {
        banner.style.display = 'none';
        _warningTimer = null;
      }, 5000);
    };

  })();
}
