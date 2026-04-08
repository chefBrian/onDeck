import Foundation

/// Applies RFC 6902 JSON Patch operations to in-memory JSON objects.
enum JSONPatch {

    /// Applies an array of patch operations to a JSON object.
    /// Throws if a patch path cannot be resolved.
    static func apply(_ patches: [[String: Any]], to json: inout Any) throws {
        for patch in patches {
            guard let op = patch["op"] as? String,
                  let path = patch["path"] as? String else { continue }

            let components = path.split(separator: "/").map(String.init)
            guard !components.isEmpty else { continue }

            switch op {
            case "replace":
                let value = patch["value"] as Any
                try setValueAtPath(components, in: &json, to: value, insert: false)
            case "add":
                let value = patch["value"] as Any
                try setValueAtPath(components, in: &json, to: value, insert: true)
            case "remove":
                try removeValueAtPath(components, in: &json)
            default:
                break
            }
        }
    }

    // MARK: - Private

    private static func setValueAtPath(
        _ path: [String], in json: inout Any, to value: Any, insert: Bool
    ) throws {
        guard !path.isEmpty else { return }

        if path.count == 1 {
            let key = path[0]
            if var dict = json as? [String: Any] {
                dict[key] = value
                json = dict
            } else if var arr = json as? [Any] {
                if key == "-" {
                    arr.append(value)
                } else if let index = Int(key) {
                    if insert && index <= arr.count {
                        arr.insert(value, at: index)
                    } else if index < arr.count {
                        arr[index] = value
                    } else {
                        arr.append(value)
                    }
                }
                json = arr
            }
            return
        }

        let key = path[0]
        let rest = Array(path.dropFirst())

        if var dict = json as? [String: Any] {
            var child: Any = dict[key] ?? [String: Any]()
            try setValueAtPath(rest, in: &child, to: value, insert: insert)
            dict[key] = child
            json = dict
        } else if var arr = json as? [Any], let index = Int(key), index < arr.count {
            var child = arr[index]
            try setValueAtPath(rest, in: &child, to: value, insert: insert)
            arr[index] = child
            json = arr
        }
    }

    private static func removeValueAtPath(_ path: [String], in json: inout Any) throws {
        guard !path.isEmpty else { return }

        if path.count == 1 {
            let key = path[0]
            if var dict = json as? [String: Any] {
                dict.removeValue(forKey: key)
                json = dict
            } else if var arr = json as? [Any], let index = Int(key), index < arr.count {
                arr.remove(at: index)
                json = arr
            }
            return
        }

        let key = path[0]
        let rest = Array(path.dropFirst())

        if var dict = json as? [String: Any], var child = dict[key] {
            try removeValueAtPath(rest, in: &child)
            dict[key] = child
            json = dict
        } else if var arr = json as? [Any], let index = Int(key), index < arr.count {
            var child = arr[index]
            try removeValueAtPath(rest, in: &child)
            arr[index] = child
            json = arr
        }
    }
}
