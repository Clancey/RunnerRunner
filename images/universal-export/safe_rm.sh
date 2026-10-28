# Sourced by CI scripts:  . tools/ci/safe_rm.sh;  safe_rm <path>...
#
# Recursive delete that never reaches into a mounted filesystem. A cancelled
# job once ran `rm -rf` on a scratch directory whose guest SMB mount of
# tower's appdata share failed to unmount, and deleted most of the share.
#
# For each path, safe_rm:
#   1. refuses /, top-level directories and $HOME;
#   2. refuses (prints ::error::, returns 1) when a mount point is the path
#      or lies under it, checked in the mount table with the path resolved
#      (macOS lists /private/var for /var);
#   3. makes a fresh, randomly named directory beside it (`.safe_rm.*`) and
#      renames the path into it as `payload` with a bare rename(2), so a mount
#      made on the old name from now on is not deleted;
#   4. checks again: the payload must be on its parent's device (macOS lets a
#      mount point be renamed) and nothing may be mounted in the wrapper; if
#      not, the path is put back and refused;
#   5. deletes the wrapper with BSD `rm -x` / GNU `--one-file-system`, which
#      never cross into another filesystem. Deleting the wrapper rather than
#      the payload means that even a mount landing on the payload itself
#      after step 4 sits below the delete root, where -x stops.
# Missing paths are skipped. Only a mount of the same filesystem (a Linux bind
# mount) made inside the wrapper after step 4 could still be traversed.

_safe_rm_real() { # <path>: physical path, without following a final symlink
	if [[ -d "$1" && ! -L "$1" ]]; then
		(cd "$1" 2>/dev/null && pwd -P)
	else
		local dir
		dir=$(cd "$(dirname "$1")" 2>/dev/null && pwd -P) || return 1
		printf '%s/%s\n' "${dir%/}" "$(basename "$1")"
	fi
}

# A bare rename(2). Not mv, which copies and deletes when rename fails with
# EXDEV, crossing into whatever is mounted.
_safe_rm_rename() { # <from> <to>
	if command -v python3 >/dev/null 2>&1; then
		python3 -c 'import os, sys; os.rename(sys.argv[1], sys.argv[2])' "$1" "$2" 2>/dev/null
	elif command -v perl >/dev/null 2>&1; then
		perl -e 'rename($ARGV[0], $ARGV[1]) or exit 1' "$1" "$2" 2>/dev/null
	else
		echo "::error::safe_rm needs python3 or perl" >&2
		return 1
	fi
}

_safe_rm_mount_points() {
	if [[ -r /proc/self/mountinfo ]]; then
		# Field 5 is the mount point, with spaces etc. octal-escaped.
		awk '{ print $5 }' /proc/self/mountinfo | while IFS= read -r mp; do printf '%b\n' "$mp"; done
	else
		mount | sed -nE 's#^.* on (/.*) \([^()]*\)$#\1#p'
	fi
}

_safe_rm_has_mount() { # <resolved path>
	local mp
	while IFS= read -r mp; do
		[[ "$mp" == "$1" || "$mp" == "$1"/* ]] && return 0
	done < <(_safe_rm_mount_points)
	# Raw mount table too; a prefix match, so it may also refuse a sibling
	# such as "$1-2", which is the safe direction.
	mount | grep -qF " on $1"
}

_safe_rm_dev() { # <path>: device id of the path itself (a symlink is not followed)
	if [[ "$(uname -s)" == Darwin ]]; then stat -f %d "$1"; else stat -c %d "$1"; fi
}

safe_rm() {
	local path real home wrapper payload status=0
	home=$(cd "${HOME:-/}" 2>/dev/null && pwd -P)
	for path in "$@"; do
		[[ -n "$path" ]] || continue
		[[ -e "$path" || -L "$path" ]] || continue
		if ! real=$(_safe_rm_real "$path") || [[ "$real" != /* ]]; then
			echo "::error::safe_rm: cannot resolve $path; not deleting it" >&2
			status=1; continue
		fi
		if [[ "$real" != /*[!/]/*[!/]* || "$real" == "$home" ]]; then
			echo "::error::safe_rm: refusing to delete $real" >&2
			status=1; continue
		fi
		if _safe_rm_has_mount "$real" || { [[ "$path" == /* && "$path" != "$real" ]] && mount | grep -qF " on $path"; }; then
			echo "::error::safe_rm: $path is or contains a mount point; not deleting it" >&2
			status=1; continue
		fi
		wrapper=""
		if ! wrapper=$(mktemp -d "$(dirname "$real")/.safe_rm.XXXXXXXX" 2>/dev/null) \
			|| [[ "$(_safe_rm_dev "$wrapper")" != "$(_safe_rm_dev "$(dirname "$real")")" ]]; then
			echo "::error::safe_rm: could not make a directory beside $path to delete it from" >&2
			[[ -z "$wrapper" ]] || rmdir "$wrapper" 2>/dev/null || true
			status=1; continue
		fi
		payload=$wrapper/payload
		if ! _safe_rm_rename "$real" "$payload"; then
			echo "::error::safe_rm: could not move $path aside to delete it" >&2
			rmdir "$wrapper" 2>/dev/null || true
			status=1; continue
		fi
		if [[ -n "${_SAFE_RM_TEST_HOOK:-}" ]]; then "$_SAFE_RM_TEST_HOOK" "$real" "$payload"; fi
		if [[ "$(_safe_rm_dev "$payload")" != "$(_safe_rm_dev "$wrapper")" ]] \
			|| _safe_rm_has_mount "$wrapper"; then
			echo "::error::safe_rm: a filesystem is mounted on or inside $path; not deleting it" >&2
			if _safe_rm_rename "$payload" "$real"; then
				rmdir "$wrapper" 2>/dev/null || true
			else
				echo "::error::safe_rm: left it at $payload" >&2
			fi
			status=1; continue
		fi
		if [[ -n "${_SAFE_RM_TEST_HOOK_LATE:-}" ]]; then "$_SAFE_RM_TEST_HOOK_LATE" "$real" "$payload"; fi
		if [[ "$(uname -s)" == Darwin ]]; then
			rm -rfx -- "$wrapper" || status=1
		else
			rm -rf --one-file-system -- "$wrapper" || status=1
		fi
	done
	return "$status"
}
