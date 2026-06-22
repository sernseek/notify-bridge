//! notify-bridge host receiver.
//!
//! A tiny blocking HTTP server that accepts `POST /notify` requests from the
//! Windows agent and re-emits each one as a desktop notification through the
//! freedesktop spec (`org.freedesktop.Notifications`, via notify-rust). Intended
//! to run as a `systemd --user` service alongside the user's Wayland session.

use std::collections::VecDeque;
use std::io::Read;
use std::sync::Mutex;

use notify_rust::Notification;
use serde::Deserialize;
use tiny_http::{Header, Method, Response, Server};

/// One notification as sent by the guest agent. See PROTOCOL.md.
#[derive(Debug, Deserialize)]
struct Incoming {
    #[serde(default)]
    id: String,
    #[serde(default)]
    app: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    body: String,
    #[serde(default)]
    #[allow(dead_code)]
    timestamp: String,
}

struct Config {
    bind: String,
    token: Option<String>,
    app_prefix: bool,
}

fn config_from_env() -> Config {
    Config {
        bind: std::env::var("NOTIFY_BRIDGE_BIND")
            .unwrap_or_else(|_| "127.0.0.1:8787".to_string()),
        token: std::env::var("NOTIFY_BRIDGE_TOKEN")
            .ok()
            .filter(|s| !s.is_empty()),
        app_prefix: std::env::var("NOTIFY_BRIDGE_APP_PREFIX")
            .map(|v| v != "0")
            .unwrap_or(true),
    }
}

/// Small fixed-size ring of recently seen ids, so agent restarts that re-send
/// the same notification don't pop a duplicate on the desktop.
struct RecentIds {
    cap: usize,
    order: VecDeque<String>,
}

impl RecentIds {
    fn new(cap: usize) -> Self {
        RecentIds {
            cap,
            order: VecDeque::with_capacity(cap),
        }
    }

    /// Returns true if `id` is new (and records it); false if already seen.
    /// Empty ids are always treated as new (nothing to dedup against).
    fn insert(&mut self, id: &str) -> bool {
        if id.is_empty() {
            return true;
        }
        if self.order.iter().any(|seen| seen == id) {
            return false;
        }
        if self.order.len() == self.cap {
            self.order.pop_front();
        }
        self.order.push_back(id.to_string());
        true
    }
}

fn header_value<'a>(headers: &'a [Header], name: &'static str) -> Option<&'a str> {
    headers
        .iter()
        .find(|h| h.field.equiv(name))
        .map(|h| h.value.as_str())
}

fn main() {
    let cfg = config_from_env();
    let server = match Server::http(&cfg.bind) {
        Ok(s) => s,
        Err(e) => {
            eprintln!("notify-bridge: failed to bind {}: {e}", cfg.bind);
            std::process::exit(1);
        }
    };
    eprintln!("notify-bridge: listening on http://{}", cfg.bind);
    if cfg.token.is_some() {
        eprintln!("notify-bridge: token auth enabled");
    }

    let recent = Mutex::new(RecentIds::new(256));

    for mut request in server.incoming_requests() {
        let method = request.method().clone();
        let url = request.url().to_string();
        let path = url.split('?').next().unwrap_or("");

        // Liveness probe.
        if method == Method::Get && path == "/health" {
            let _ = request.respond(Response::from_string("ok"));
            continue;
        }

        if !(method == Method::Post && path == "/notify") {
            let _ = request.respond(Response::from_string("").with_status_code(405));
            continue;
        }

        // Token check.
        if let Some(expected) = &cfg.token {
            let provided = header_value(request.headers(), "X-Bridge-Token");
            if provided != Some(expected.as_str()) {
                let _ = request.respond(Response::from_string("").with_status_code(401));
                continue;
            }
        }

        // Bounded read to avoid a hostile/huge body wedging the service.
        let mut body = String::new();
        if request.as_reader().take(64 * 1024).read_to_string(&mut body).is_err() {
            let _ = request.respond(Response::from_string("").with_status_code(400));
            continue;
        }

        let note: Incoming = match serde_json::from_str(&body) {
            Ok(n) => n,
            Err(_) => {
                let _ = request.respond(Response::from_string("").with_status_code(400));
                continue;
            }
        };

        let is_new = recent.lock().map(|mut r| r.insert(&note.id)).unwrap_or(true);
        if is_new {
            emit(&cfg, &note);
        }

        let _ = request.respond(Response::from_string("").with_status_code(204));
    }
}

fn emit(cfg: &Config, note: &Incoming) {
    // Build a sensible summary/body even when one side is empty.
    let app = if note.app.is_empty() { "Windows" } else { &note.app };
    let summary = match (cfg.app_prefix, note.title.trim().is_empty()) {
        (true, false) => format!("{app}: {}", note.title),
        (true, true) => app.to_string(),
        (false, false) => note.title.clone(),
        (false, true) => app.to_string(),
    };

    let mut builder = Notification::new();
    builder.summary(&summary).appname(app);
    if !note.body.trim().is_empty() {
        builder.body(&note.body);
    }

    if let Err(e) = builder.show() {
        eprintln!("notify-bridge: failed to show notification: {e}");
    }
}
